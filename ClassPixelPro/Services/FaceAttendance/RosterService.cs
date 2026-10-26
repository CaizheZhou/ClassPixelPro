using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Services.FaceAttendance;

/// <summary>
/// 一个学生的一条人脸样本。
/// </summary>
public sealed class FaceSample
{
    /// <summary>特征向量（SFace 128 维 / ArcFace 512 维）。</summary>
    public float[] Vector { get; set; } = Array.Empty<float>();

    /// <summary>样本来源（摄像头录入 / 照片导入 / 远程录入），用于界面显示。</summary>
    public string Source { get; set; } = "";

    /// <summary>采集时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>对应的人脸小图文件名（相对 faces 目录），可为空。</summary>
    public string SnapshotFile { get; set; } = "";
}

/// <summary>
/// 一位学生。
/// </summary>
public sealed class Student
{
    /// <summary>稳定 Id。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>姓名。</summary>
    public string Name { get; set; } = "";

    /// <summary>班级名（可空）。</summary>
    public string ClassName { get; set; } = "";

    /// <summary>是否参与考勤。关闭后既不比对也不计迟到。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>今日是否请假。请假的人不计迟到。</summary>
    public bool OnLeave { get; set; }

    /// <summary>备注。</summary>
    public string Note { get; set; } = "";

    /// <summary>该学生的人脸样本。</summary>
    public List<FaceSample> Faces { get; set; } = new();

    /// <summary>已录入的人脸样本数，供界面直接绑定。</summary>
    [JsonIgnore]
    public int FaceCount => Faces.Count;

    /// <summary>是否已经录过人脸。</summary>
    [JsonIgnore]
    public bool HasFace => Faces.Count > 0;
}

/// <summary>
/// 花名册与人脸库的磁盘文档。
/// </summary>
public sealed class RosterDocument
{
    /// <summary>格式版本，用于将来迁移。</summary>
    public int Version { get; set; } = 1;

    /// <summary>学生列表。</summary>
    public List<Student> Students { get; set; } = new();
}

/// <summary>
/// 人脸库里的一条记录：某个学生的一条人脸样本。
/// </summary>
public sealed class GalleryEntry
{
    /// <summary>样本所属学生。</summary>
    public Student Student { get; init; } = null!;

    /// <summary>特征向量。</summary>
    public float[] Feature { get; init; } = Array.Empty<float>();
}

/// <summary>
/// 花名册与人脸库。数据保存在配置目录的 roster.json 中，
/// 人脸特征向量随花名册一起保存，<b>只落本机</b>，无需任何联网或云端服务。
/// </summary>
public sealed class RosterService
{
    /// <summary>
    /// 单个学生最多保留的人脸样本数。样本越多样本越好，但过多收益递减且拖慢比对。
    /// </summary>
    public const int MaxSamplesPerStudent = 12;

    private readonly ILogger<RosterService> _logger;
    private readonly object _sync = new();
    private RosterDocument _document = new();
    private string _filePath = "";
    private bool _loaded;

    public RosterService(ILogger<RosterService> logger)
    {
        _logger = logger;
    }

    /// <summary>花名册文件路径。</summary>
    public string FilePath
    {
        get
        {
            EnsureLoaded();
            return _filePath;
        }
    }

    /// <summary>花名册数据。首次访问时会自动从磁盘载入。</summary>
    public RosterDocument Document
    {
        get
        {
            EnsureLoaded();
            return _document;
        }
    }

    /// <summary>学生列表。</summary>
    public List<Student> Students
    {
        get
        {
            EnsureLoaded();
            return _document.Students;
        }
    }

    /// <summary>花名册或人脸库发生变化时触发。</summary>
    public event EventHandler? RosterChanged;

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            Load();
        }
    }

    /// <summary>从磁盘载入花名册。</summary>
    public void Load()
    {
        lock (_sync)
        {
            if (_loaded) return;

            _filePath = Path.Combine(FaceAttendancePaths.RootFolder, "roster.json");
            RosterDocument? loaded = null;
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    loaded = System.Text.Json.JsonSerializer.Deserialize<RosterDocument>(json,
                        new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                            AllowTrailingCommas = true,
                        });
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "载入花名册失败，将使用空花名册");
            }

            _document = loaded ?? new RosterDocument();
            _document.Students ??= new List<Student>();
            foreach (var student in _document.Students)
            {
                student.Faces ??= new List<FaceSample>();
                foreach (var sample in student.Faces)
                {
                    sample.Vector ??= Array.Empty<float>();
                }
            }

            _loaded = true;
        }

        RaiseChanged(save: false);
    }

    /// <summary>保存花名册到磁盘。</summary>
    public void Save()
    {
        lock (_sync)
        {
            if (!_loaded || string.IsNullOrEmpty(_filePath)) return;

            try
            {
                FaceAttendancePaths.EnsureDirectory(FaceAttendancePaths.RootFolder);
                var json = System.Text.Json.JsonSerializer.Serialize(_document,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                // 先写临时文件再替换，避免写一半断电把花名册（含全部人脸特征）写坏。
                var temp = _filePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _filePath, true);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "保存花名册失败");
            }
        }
    }

    /// <summary>
    /// 通知界面数据已变化。
    /// </summary>
    /// <param name="save">
    /// 是否顺便落盘。批量导入几百张照片时，每加一条就整份序列化一次会非常慢，
    /// 这种场合传 <c>false</c>，等整批做完再统一保存一次。
    /// </param>
    public void RaiseChanged(bool save = true)
    {
        if (save)
        {
            Save();
        }

        RosterChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按姓名查一位同学；className 为空时只看姓名。</summary>
    public Student? FindByName(string name, string? className = null)
    {
        var target = (name ?? "").Trim();
        if (target.Length == 0) return null;

        lock (_sync)
        {
            return Students.FirstOrDefault(x =>
                string.Equals(x.Name, target, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(className) || string.Equals(x.ClassName, className, StringComparison.Ordinal)));
        }
    }

    /// <summary>参与考勤的学生（已启用且未请假）。</summary>
    public List<Student> GetActiveStudents()
    {
        lock (_sync)
        {
            return Students.Where(x => x.Enabled && !x.OnLeave).ToList();
        }
    }

    /// <summary>新增一位学生。</summary>
    public Student AddStudent(string name, string className = "")
    {
        var student = new Student { Name = name.Trim(), ClassName = className.Trim() };
        lock (_sync)
        {
            Students.Add(student);
        }

        RaiseChanged();
        return student;
    }

    /// <summary>重命名一位学生（或改班级）。姓名重复时返回 false。</summary>
    public bool RenameStudent(Student student, string newName, string? newClassName = null)
    {
        var name = (newName ?? "").Trim();
        if (name.Length == 0) return false;

        var className = (newClassName ?? student.ClassName ?? "").Trim();
        lock (_sync)
        {
            if (Students.Any(x => !ReferenceEquals(x, student)
                                  && string.Equals(x.Name, name, StringComparison.Ordinal)
                                  && string.Equals(x.ClassName ?? "", className, StringComparison.Ordinal)))
            {
                return false;
            }

            student.Name = name;
            student.ClassName = className;
        }

        RaiseChanged();
        return true;
    }

    /// <summary>删除一位学生。</summary>
    public bool RemoveStudent(Student student)
    {
        bool removed;
        lock (_sync)
        {
            removed = Students.Remove(student);
        }

        if (removed)
        {
            TryDeleteFaceFolder(student);
            RaiseChanged();
        }

        return removed;
    }

    /// <summary>为一位学生追加人脸样本。</summary>
    public bool AddFaceSample(Student student, float[] vector, string source, string? snapshotFile, bool save = true)
    {
        if (vector.Length == 0) return false;

        lock (_sync)
        {
            if (student.Faces.Count >= MaxSamplesPerStudent)
            {
                // 超出上限时替换掉最早的一条，保证样本不过期也不无限增长。
                student.Faces.RemoveAt(0);
            }

            student.Faces.Add(new FaceSample
            {
                Vector = vector,
                Source = source,
                CreatedAt = DateTime.Now,
                SnapshotFile = snapshotFile ?? "",
            });
        }

        RaiseChanged(save);
        return true;
    }

    /// <summary>删除某位学生的单条人脸样本。</summary>
    /// <param name="student">学生。</param>
    /// <param name="index">样本在 <see cref="Student.Faces"/> 中的下标（界面列表的顺序）。</param>
    public bool RemoveFaceSample(Student student, int index)
    {
        lock (_sync)
        {
            if (index < 0 || index >= student.Faces.Count) return false;
            student.Faces.RemoveAt(index);
        }

        RaiseChanged();
        return true;
    }

    /// <summary>清空某位学生的全部人脸样本。</summary>
    public void ClearFaces(Student student)
    {
        lock (_sync)
        {
            student.Faces.Clear();
        }

        TryDeleteFaceFolder(student);
        RaiseChanged();
    }

    /// <summary>
    /// 构建用于比对的人脸库。
    /// <para>
    /// 这里**不要**按固定维度过滤：换成 ArcFace（512 维）之后如果还写死
    /// <c>Vector.Length == 128</c>，所有人脸都会被过滤掉，人脸库变成空的，
    /// 界面却报「还没有录入人脸样本」，非常难查。维度是否匹配交给
    /// <see cref="FaceFeatureExtractor.Similarity"/> 判断（维度不同直接返回 0）。
    /// </para>
    /// </summary>
    public List<GalleryEntry> BuildGallery()
    {
        var gallery = new List<GalleryEntry>();
        lock (_sync)
        {
            foreach (var student in Students)
            {
                if (!student.Enabled || student.OnLeave) continue;

                foreach (var sample in student.Faces)
                {
                    if (sample.Vector.Length > 0)
                    {
                        gallery.Add(new GalleryEntry { Student = student, Feature = sample.Vector });
                    }
                }
            }
        }

        return gallery;
    }

    /// <summary>
    /// 按文件名猜这张照片属于花名册里的谁。
    /// <para>
    /// 「ABCD 四人，批量导入 806班A.jpg、806班B.jpg」这种场景就是靠这里：
    /// 把扩展名去掉后去花名册里找名字，命中就直接自动导入，不用一张张手点。
    /// </para>
    /// </summary>
    public NameMatchResult MatchByFileName(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName ?? "") ?? "";
        if (baseName.Length == 0)
        {
            return NameMatchResult.None("文件名里没有可用字符");
        }

        var normalized = NormalizeForMatch(baseName);

        Student? best = null;
        var bestScore = 0;
        var tie = false;

        lock (_sync)
        {
            foreach (var student in Students)
            {
                var name = NormalizeForMatch(student.Name);
                if (name.Length == 0) continue;

                var score = ScoreName(normalized, name);
                if (score <= 0) continue;

                // 文件名里同时出现了该同学的班级，多半就是他，加分。
                if (!string.IsNullOrWhiteSpace(student.ClassName)
                    && baseName.Contains(student.ClassName, StringComparison.Ordinal))
                {
                    score += 40;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = student;
                    tie = false;
                }
                else if (score == bestScore && best != null && !ReferenceEquals(best, student))
                {
                    tie = true;
                }
            }
        }

        if (best == null)
        {
            return NameMatchResult.None("文件名里没有匹配到花名册中的同学");
        }

        return tie
            ? NameMatchResult.Ambiguous("文件名同时能对上多位同学，无法确定是谁")
            : new NameMatchResult { Student = best, Score = bestScore };
    }

    /// <summary>去掉空白与常见分隔符，拉丁字母统一小写，便于比较。</summary>
    private static string NormalizeForMatch(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch)) continue;

            if (ch is '-' or '_' or '.' or '·' or '(' or ')' or '[' or ']'
                or '（' or '）' or '【' or '】' or '、' or '/' or '\\')
            {
                continue;
            }

            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 给「文件名里出现姓名」打分。分值本身就是优先级：
    /// 完全相等 &gt; 开头/结尾 &gt; 中途出现。
    /// </summary>
    private static int ScoreName(string normalizedBase, string normalizedName)
    {
        if (normalizedBase == normalizedName) return 1000;

        var starts = normalizedBase.StartsWith(normalizedName, StringComparison.Ordinal);
        var ends = normalizedBase.EndsWith(normalizedName, StringComparison.Ordinal);

        // 短的姓名（如单个字母 A、两个字的中文名）很容易在无关文本里撞上，
        // 所以只有出现在开头或结尾才给高分，中途出现只给很低的分，
        // 这样「806班A-1.jpg」能对上 A，而「IMG20260912」不会把所有人对上。
        if (normalizedName.Length <= 2)
        {
            if (starts) return 600 + normalizedName.Length;
            if (ends) return 580 + normalizedName.Length;
            return normalizedBase.Contains(normalizedName, StringComparison.Ordinal) ? 50 + normalizedName.Length : 0;
        }

        if (starts) return 500 + normalizedName.Length;
        if (ends) return 490 + normalizedName.Length;
        return normalizedBase.Contains(normalizedName, StringComparison.Ordinal) ? 100 + normalizedName.Length : 0;
    }

    /// <summary>
    /// 从文本批量导入花名册。每行一位同学，支持「姓名」或「姓名,班级」或「姓名 班级」。
    /// </summary>
    public int ImportFromText(string text, string defaultClassName)
    {
        var added = 0;
        var separators = new[] { ',', '，', '\t', ' ' };
        lock (_sync)
        {
            foreach (var rawLine in (text ?? "").Split('\n'))
            {
                var line = rawLine.Trim().TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith('#')) continue;

                var parts = line.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) continue;

                var name = parts[0];
                var className = parts.Length > 1 ? parts[1] : defaultClassName;
                if (Students.Any(x => x.Name == name && x.ClassName == className)) continue;

                Students.Add(new Student { Name = name, ClassName = className });
                added++;
            }
        }

        if (added > 0)
        {
            RaiseChanged();
        }

        return added;
    }

    /// <summary>导出花名册为 CSV 文本。</summary>
    public string ExportToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("姓名,班级,请假,参与考勤,人脸样本数,备注");
        lock (_sync)
        {
            foreach (var student in Students)
            {
                sb.AppendLine(string.Join(",",
                    Escape(student.Name),
                    Escape(student.ClassName),
                    student.OnLeave ? "是" : "否",
                    student.Enabled ? "是" : "否",
                    student.Faces.Count.ToString(),
                    Escape(student.Note)));
            }
        }

        return sb.ToString();
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Contains(',') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private void TryDeleteFaceFolder(Student student)
    {
        try
        {
            var dir = Path.Combine(FaceAttendancePaths.FacesFolder, student.Id);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "删除学生人脸目录失败：{Name}", student.Name);
        }
    }
}

/// <summary>
/// 文件名匹配花名册的结果。
/// </summary>
public sealed class NameMatchResult
{
    /// <summary>匹配到的学生；未匹配到为 null。</summary>
    public Student? Student { get; init; }

    /// <summary>匹配置信度分值。</summary>
    public int Score { get; init; }

    /// <summary>是否同时能对上多位同学。</summary>
    public bool IsAmbiguous { get; init; }

    /// <summary>失败/歧义的原因说明。</summary>
    public string Reason { get; init; } = "";

    /// <summary>构造一个「没匹配上」的结果。</summary>
    public static NameMatchResult None(string reason) => new() { Reason = reason };

    /// <summary>构造一个「匹配到多位、无法确定」的结果。</summary>
    public static NameMatchResult Ambiguous(string reason) => new() { IsAmbiguous = true, Reason = reason };
}
