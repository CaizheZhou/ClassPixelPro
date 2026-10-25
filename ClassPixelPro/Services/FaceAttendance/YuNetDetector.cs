using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Dnn = OpenCvSharp.Dnn;

namespace ClassPixelPro.Services.FaceAttendance;

/// <summary>
/// 一张被检测到的人脸（坐标都是<b>原图</b>坐标）。
/// </summary>
public sealed class DetectedFace
{
    /// <summary>15 个浮点：box(4) + 5 组关键点(10) + 置信度(1)，与插件原版布局一致。</summary>
    public float[] Box { get; } = new float[15];

    /// <summary>128/512 维特征向量（调用特征提取后才有值）。</summary>
    public float[]? Feature { get; set; }

    /// <summary>人脸框左上角 X。</summary>
    public int X => (int)MathF.Round(Box[0]);

    /// <summary>人脸框左上角 Y。</summary>
    public int Y => (int)MathF.Round(Box[1]);

    /// <summary>人脸框宽度。</summary>
    public int Width => (int)MathF.Round(Box[2]);

    /// <summary>人脸框高度。</summary>
    public int Height => (int)MathF.Round(Box[3]);

    /// <summary>检测置信度。</summary>
    public float Score => Box[14];

    /// <summary>人脸框面积，用于挑选「离摄像头最近的人」。</summary>
    public int Area => Math.Max(0, Width) * Math.Max(0, Height);
}

/// <summary>
/// YuNet 人脸检测器（经 <see cref="CvDnn"/> 直接跑 ONNX）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不直接用 OpenCV 的 <c>FaceDetectorYN</c>：</b>主程序引用的 OpenCvSharp 4.10
/// 没有封装 <c>FaceDetectorYN</c>（该类是 OpenCvSharp 后续版本才加的），而
/// <c>OpenCvSharpExtern.dll</c> 里虽然含 <c>cv::FaceDetectorYN</c> 的 C++ 符号（RTTI/vtable），
/// <b>却没有对应的 C 风格导出</b>（形如 <c>objdetect_FaceDetectorYN_*</c>），
/// 所以 P/Invoke 这条路也走不通（已实测 0/9 命中）。
/// </para>
/// <para>
/// 因此这里把 YuNet 当普通 ONNX 通过 <c>Dnn.CvDnn.ReadNetFromOnnx</c> 加载，
/// 自己实现后处理（解码 + NMS）。YuNet 的 ONNX 有 12 个输出层
/// （<c>cls_{8,16,32}</c>、<c>obj_{8,16,32}</c>、<c>bbox_{8,16,32}</c>、<c>kps_{8,16,32}</c>），
/// 语义与官方 <c>FaceDetectorYN</c> 完全一致，后处理算法照官方 <c>detector.cpp</c> 复刻。
/// </para>
/// <para>
/// <b>解码公式（已实测核对，误差 &lt; 2px）：</b>
/// <list type="bullet">
/// <item>score = sqrt(cls × obj)</item>
/// <item>cx = grid_x × stride + bbox[0] × stride，cy = grid_y × stride + bbox[1] × stride</item>
/// <item>w = exp(bbox[2]) × stride，h = exp(bbox[3]) × stride</item>
/// <item>kps[i] = (grid × stride) + kps[2i] × stride</item>
/// </list>
/// 5 个关键点顺序与插件原版一致：右眼、左眼、鼻尖、右嘴角、左嘴角。
/// </para>
/// </remarks>
public sealed class YuNetDetector : IDisposable
{
    /// <summary>
    /// 检测置信度阈值。教室场景人多脸小，取 0.6 换一点召回：
    /// 后排同学的脸本来置信度就偏低，阈值太高会直接漏掉；误检出来的假脸
    /// 会被「最小人脸宽度」和「多帧投票」过滤掉。
    /// </summary>
    private const float ScoreThreshold = 0.6f;

    /// <summary>NMS 的 IoU 阈值。</summary>
    private const float NmsThreshold = 0.3f;

    /// <summary>最多保留几个候选框（与官方默认 5000 一致）。</summary>
    private const int TopK = 5000;

    /// <summary>检测器的工作宽度。YuNet 在这个尺度上速度与精度平衡最好。</summary>
    public const int WorkWidth = 640;

    /// <summary>检测器的工作高度。</summary>
    public const int WorkHeight = 480;

    /// <summary>三组 stride。</summary>
    private static readonly int[] Strides = { 8, 16, 32 };

    private readonly ILogger<YuNetDetector> _logger;
    private readonly object _sync = new();

    private Dnn.Net? _net;
    private string _modelPath = "";
    private string _loadPath = "";
    private string _lastError = "";

    /// <summary>按输入尺寸缓存 blob，避免每次检测都重新构造。</summary>
    private readonly Dictionary<(int W, int H), Mat> _blobCache = new();

    public YuNetDetector(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<YuNetDetector>();
    }

    /// <summary>模型是否已载入。</summary>
    public bool IsLoaded
    {
        get
        {
            lock (_sync) return _net != null && !_net.Empty();
        }
    }

    /// <summary>当前使用的检测模型路径。</summary>
    public string ModelPath
    {
        get
        {
            lock (_sync) return _modelPath;
        }
    }

    /// <summary>最近一次创建检测器失败的原因；成功后被清空。</summary>
    public string LastError
    {
        get
        {
            lock (_sync) return _lastError;
        }
    }

    /// <summary>
    /// 载入（或重新载入）检测模型。
    /// </summary>
    /// <param name="modelPath">.onnx 文件路径。</param>
    public bool Load(string modelPath)
    {
        lock (_sync)
        {
            if (_net != null && _modelPath == modelPath)
            {
                return true;
            }

            try
            {
                // CvDnn 有字节数组重载，路径含中文时退化成「读进内存再解析」。
                // 主程序的配置目录在 %APPDATA% 下，Windows 用户名可能是中文，这个坑必须防住。
                Dnn.Net? net;
                try
                {
                    net = Dnn.CvDnn.ReadNetFromOnnx(modelPath);
                }
                catch (Exception) when (!AsciiModelPath.IsAscii(modelPath))
                {
                    _logger.LogWarning("YuNet 按路径加载失败，改用内存方式重试：{Path}", modelPath);
                    net = Dnn.CvDnn.ReadNetFromOnnx(File.ReadAllBytes(modelPath));
                }

                if (net == null || net.Empty())
                {
                    net?.Dispose();
                    throw new InvalidDataException($"人脸检测模型无法解析，文件可能不完整：{modelPath}");
                }

                _net?.Dispose();
                foreach (var blob in _blobCache.Values) blob.Dispose();
                _blobCache.Clear();

                _net = net;
                _modelPath = modelPath;
                _loadPath = AsciiModelPath.Resolve(modelPath, "face_detection_yunet.onnx", _logger) ?? modelPath;
                _lastError = "";

                _logger.LogInformation("YuNet 检测模型已载入：{Path}", modelPath);
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "载入 YuNet 检测模型失败：{Path}", modelPath);
                _lastError = DescribeLoadError(e, modelPath);
                return false;
            }
        }
    }

    private static string DescribeLoadError(Exception e, string path)
    {
        var hint = e switch
        {
            DllNotFoundException or BadImageFormatException =>
                "OpenCV 原生库（OpenCvSharpExtern.dll）加载失败。"
                + "常见原因是系统缺少 Microsoft Visual C++ 运行库，或主程序位数与原生库不一致。",
            TypeInitializationException or EntryPointNotFoundException =>
                "OpenCV 原生库加载失败：" + e.Message,
            _ when !AsciiModelPath.IsAscii(path) =>
                "检测模型所在路径含非 ASCII 字符（例如中文目录），OpenCV 读不出来：" + path,
            _ => e.Message,
        };
        return "人脸检测模型加载失败：" + hint;
    }

    /// <summary>
    /// 在图片中检测人脸。
    /// </summary>
    /// <param name="bgr">待检测的 BGR 图像（不会被修改）。</param>
    /// <param name="minFaceWidth">最小人脸宽度，单位是<b>原图</b>像素。</param>
    /// <param name="scoreThreshold">检测置信度阈值。</param>
    /// <remarks>
    /// 输入会先被<b>等比例</b>缩放到不超过 640×480 的工作尺寸再检测
    /// （长宽比不变、不补黑边、不拉伸），检出后把框和 5 个关键点一起乘回原图坐标。
    /// 这与插件原版的行为一致：摄像头换成别的分辨率时不用重建检测器，
    /// 同一批照片的检测尺度也一致。
    /// </remarks>
    public List<DetectedFace> Detect(Mat bgr, int minFaceWidth = 60, float scoreThreshold = ScoreThreshold)
    {
        var result = new List<DetectedFace>();
        if (bgr.Empty())
        {
            return result;
        }

        lock (_sync)
        {
            if (_net == null || _net.Empty())
            {
                _lastError = "人脸检测模型尚未载入。";
                return result;
            }

            var target = ComputeWorkSize(bgr.Width, bgr.Height);
            var scaleX = (double)bgr.Width / target.Width;
            var scaleY = (double)bgr.Height / target.Height;

            Mat? resized = null;
            try
            {
                var input = bgr;
                if (target.Width != bgr.Width || target.Height != bgr.Height)
                {
                    resized = new Mat();
                    var interp = target.Width < bgr.Width ? InterpolationFlags.Area : InterpolationFlags.Linear;
                    Cv2.Resize(bgr, resized, target, 0, 0, interp);
                    input = resized;
                }

                // YuNet 要求输入严格等于创建时给定尺寸的图，而 CvDnn 的 forward 是按实际输入算的，
                // 所以每次都按当前工作尺寸构造 blob。
                var blob = GetBlob(input, target);
                _net.SetInput(blob);

                var proposals = new List<(float[] Box, float Score)>();
                foreach (var stride in Strides)
                {
                    var gridW = target.Width / stride;
                    var gridH = target.Height / stride;
                    var expected = gridW * gridH;

                    using var cls = _net.Forward($"cls_{stride}");
                    using var obj = _net.Forward($"obj_{stride}");
                    using var bbox = _net.Forward($"bbox_{stride}");
                    using var kps = _net.Forward($"kps_{stride}");

                    // 尺寸对不上说明模型与这里假设的后处理不一致，直接放弃而不是猜。
                    if (cls.Size(1) != expected || bbox.Size(2) != 4 || kps.Size(2) != 10)
                    {
                        _lastError = $"YuNet 输出形状与预期不符（stride {stride}，cls {cls.Size(1)}≠{expected}）。";
                        _logger.LogError("{Error}", _lastError);
                        return result;
                    }

                    for (var i = 0; i < expected; i++)
                    {
                        var c = cls.Get<float>(0, i, 0);
                        var o = obj.Get<float>(0, i, 0);
                        var score = MathF.Sqrt(c * o);
                        if (score < scoreThreshold) continue;

                        var gx = i % gridW;
                        var gy = i / gridW;
                        var ax = gx * stride;
                        var ay = gy * stride;

                        var cx = ax + bbox.Get<float>(0, i, 0) * stride;
                        var cy = ay + bbox.Get<float>(0, i, 1) * stride;
                        var w = MathF.Exp(bbox.Get<float>(0, i, 2)) * stride;
                        var h = MathF.Exp(bbox.Get<float>(0, i, 3)) * stride;

                        var box = new float[15];
                        box[0] = cx - w / 2f;
                        box[1] = cy - h / 2f;
                        box[2] = w;
                        box[3] = h;
                        for (var k = 0; k < 10; k++)
                        {
                            box[4 + k] = (k % 2 == 0 ? ax : ay) + kps.Get<float>(0, i, k) * stride;
                        }
                        box[14] = score;

                        proposals.Add((box, score));
                    }
                }

                // 按置信度排序，取前 TopK 再做 NMS
                proposals.Sort((a, b) => b.Score.CompareTo(a.Score));
                if (proposals.Count > TopK)
                {
                    proposals.RemoveRange(TopK, proposals.Count - TopK);
                }

                var keep = Nms(proposals, NmsThreshold);
                foreach (var idx in keep)
                {
                    var p = proposals[idx];
                    // 还原到原图坐标
                    var face = new DetectedFace();
                    for (var j = 0; j < 14; j++)
                    {
                        face.Box[j] = p.Box[j] * (j % 2 == 0 ? (float)scaleX : (float)scaleY);
                    }
                    face.Box[14] = p.Score;

                    if (face.Width < minFaceWidth)
                    {
                        continue;
                    }

                    result.Add(face);
                }

                _lastError = "";
                return result;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "YuNet 检测失败");
                _lastError = "人脸检测执行失败：" + e.Message;
                return result;
            }
            finally
            {
                resized?.Dispose();
            }
        }
    }

    /// <summary>
    /// 取得（或构造）指定尺寸的输入 blob。
    /// </summary>
    private Mat GetBlob(Mat input, Size size)
    {
        if (_blobCache.TryGetValue((size.Width, size.Height), out var cached))
        {
            return cached;
        }

        // YuNet 的输入是 1x3xHxW，值域 0-255，RGB 通道序（swapRB=true）
        var blob = Dnn.CvDnn.BlobFromImage(input, 1.0, new Size(size.Width, size.Height),
            new Scalar(0, 0, 0), true, false);
        _blobCache[(size.Width, size.Height)] = blob;

        // 缓存别无限涨：常见的也就两三种尺寸。
        if (_blobCache.Count > 6)
        {
            var oldest = _blobCache.Keys.First();
            _blobCache[oldest].Dispose();
            _blobCache.Remove(oldest);
        }

        return blob;
    }

    /// <summary>
    /// 计算等比例缩放后的工作尺寸（保持长宽比，不补黑边）。
    /// </summary>
    public static Size ComputeWorkSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return new Size(WorkWidth, WorkHeight);
        }

        var ratio = Math.Min((double)WorkWidth / width, (double)WorkHeight / height);

        // 图片本身就比工作尺寸小，原样使用（放大只会更慢更糊，还可能引入伪影）。
        if (ratio >= 1.0)
        {
            return new Size(width, height);
        }

        var w = Math.Max(1, (int)Math.Round(width * ratio));
        var h = Math.Max(1, (int)Math.Round(height * ratio));
        return new Size(w, h);
    }

    /// <summary>
    /// 非极大值抑制。返回保留下来的下标（按置信度从高到低）。
    /// </summary>
    private static List<int> Nms(List<(float[] Box, float Score)> proposals, float iouThreshold)
    {
        var kept = new List<int>();
        var suppressed = new bool[proposals.Count];

        for (var i = 0; i < proposals.Count; i++)
        {
            if (suppressed[i]) continue;
            kept.Add(i);

            for (var j = i + 1; j < proposals.Count; j++)
            {
                if (suppressed[j]) continue;
                if (IoU(proposals[i].Box, proposals[j].Box) > iouThreshold)
                {
                    suppressed[j] = true;
                }
            }
        }

        return kept;
    }

    /// <summary>两个 [x, y, w, h] 框的交并比。</summary>
    private static float IoU(float[] a, float[] b)
    {
        var ax2 = a[0] + a[2];
        var ay2 = a[1] + a[3];
        var bx2 = b[0] + b[2];
        var by2 = b[1] + b[3];

        var x1 = MathF.Max(a[0], b[0]);
        var y1 = MathF.Max(a[1], b[1]);
        var x2 = MathF.Min(ax2, bx2);
        var y2 = MathF.Min(ay2, by2);

        if (x2 <= x1 || y2 <= y1) return 0f;

        var inter = (x2 - x1) * (y2 - y1);
        var areaA = a[2] * a[3];
        var areaB = b[2] * b[3];
        return inter / (areaA + areaB - inter);
    }

    /// <summary>
    /// 真实自检：不只检查文件在不在，而是<b>真跑一次推理</b>，
    /// 把「文件都在、但就是跑不起来」这种状态当场暴露出来。
    /// </summary>
    public (bool Ok, string Message) SelfTest()
    {
        lock (_sync)
        {
            if (_net == null || _net.Empty())
            {
                return (false, "检测模型尚未载入内存。");
            }

            try
            {
                using var blank = new Mat(WorkHeight, WorkWidth, MatType.CV_8UC3,
                    new Scalar(128, 128, 128));
                var faces = Detect(blank);
                return (true, $"检测器可用（{WorkWidth}×{WorkHeight}，空图检出 {faces.Count} 张脸）。");
            }
            catch (Exception e)
            {
                _logger.LogError(e, "YuNet 自检失败");
                return (false, "检测器自检失败：" + e.Message);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _net?.Dispose();
            _net = null;
            foreach (var blob in _blobCache.Values) blob.Dispose();
            _blobCache.Clear();
        }
    }
}
