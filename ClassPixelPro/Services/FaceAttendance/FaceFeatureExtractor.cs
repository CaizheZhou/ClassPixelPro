using System;
using System.IO;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Dnn = OpenCvSharp.Dnn;

namespace ClassPixelPro.Services.FaceAttendance;

/// <summary>
/// 人脸特征提取器：SFace（128 维）/ ArcFace MobileFaceNet（512 维）。
/// </summary>
/// <remarks>
/// 流程与 OpenCV <c>FaceRecognizerSF</c> 一致：按 5 点关键点做 Umeyama 相似变换，
/// 把人脸摆正到 112×112，再走一次前向拿特征向量。
/// <para>
/// <b>预处理随模型而定</b>，必须跟「实际载入的那个文件」走：
/// <list type="bullet">
/// <item>SFace（OpenCV Zoo）：<c>blobFromImage(scale=1)</c>，不减均值，与 OpenCV 官方实现一致；</item>
/// <item>ArcFace（InsightFace 系）：<c>(像素 - 127.5) / 127.5</c>。</item>
/// </list>
/// 预处理对不上的话，相似度分布会整体跑偏，设置的阈值就完全失效了。
/// </para>
/// </remarks>
public sealed class FaceFeatureExtractor : IDisposable
{
    /// <summary>SFace 对齐后的输入尺寸。</summary>
    private const int AlignSize = 112;

    /// <summary>
    /// 5 点对齐参考点，取自 OpenCV face_recognize.cpp。
    /// SFace 与 ArcFace 用的是同一套 5 点模板，所以两种模型可以共用这段对齐代码。
    /// </summary>
    private static readonly double[,] AlignDst =
    {
        { 38.2946, 51.6963 },
        { 73.5318, 51.5014 },
        { 56.0252, 71.7366 },
        { 41.5493, 92.3655 },
        { 70.7299, 92.2041 },
    };

    private static readonly double[] AlignDstMean = { 56.0262, 71.9008 };

    private readonly ILogger<FaceFeatureExtractor> _logger;
    private readonly object _sync = new();

    private Dnn.Net? _net;
    private string _modelPath = "";
    private bool _isArcFace;
    private int _featureLength = 128;
    private string _lastError = "";

    public FaceFeatureExtractor(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<FaceFeatureExtractor>();
    }

    /// <summary>模型是否已载入。</summary>
    public bool IsLoaded
    {
        get
        {
            lock (_sync) return _net != null && !_net.Empty();
        }
    }

    /// <summary>当前载入的识别模型路径。</summary>
    public string ModelPath
    {
        get
        {
            lock (_sync) return _modelPath;
        }
    }

    /// <summary>当前模型是不是 ArcFace 系（决定预处理方式）。</summary>
    public bool IsArcFace
    {
        get
        {
            lock (_sync) return _isArcFace;
        }
    }

    /// <summary>特征维度（SFace 128，ArcFace 512）。</summary>
    public int FeatureLength
    {
        get
        {
            lock (_sync) return _featureLength;
        }
    }

    /// <summary>最近一次加载/推理失败的原因。</summary>
    public string LastError
    {
        get
        {
            lock (_sync) return _lastError;
        }
    }

    /// <summary>
    /// 载入（或重新载入）识别模型。
    /// </summary>
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
                // ReadNetFromOnnx 是同步阻塞的，37MB 的 SFace 载入要好几秒。
                // 调用方应放到后台线程，否则会卡住界面。
                Dnn.Net? net;
                try
                {
                    net = Dnn.CvDnn.ReadNetFromOnnx(modelPath);
                }
                catch (Exception) when (!AsciiModelPath.IsAscii(modelPath))
                {
                    // 路径含中文时退化成「读进内存再解析」，与路径彻底无关。
                    _logger.LogWarning("识别模型按路径加载失败，改用内存方式重试：{Path}", modelPath);
                    net = Dnn.CvDnn.ReadNetFromOnnx(File.ReadAllBytes(modelPath));
                }

                if (net == null || net.Empty())
                {
                    net?.Dispose();
                    throw new InvalidDataException($"人脸识别模型无法解析，文件可能不完整：{modelPath}");
                }

                _net?.Dispose();
                _net = net;
                _modelPath = modelPath;
                _isArcFace = FaceModelCatalog.IsInsightFaceStyle(modelPath);
                _lastError = "";

                _logger.LogInformation("识别模型已载入：{Path}（{Kind}）", modelPath,
                    _isArcFace ? "InsightFace 系（512 维，预处理 (x-127.5)/127.5）" : "OpenCV 系（128 维，预处理 scale=1）");
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "载入识别模型失败：{Path}", modelPath);
                _lastError = "人脸识别模型加载失败：" + e.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// 提取一张人脸的特征向量。关键点必须是<b>原图</b>坐标。
    /// </summary>
    /// <returns>特征向量；失败返回 null。</returns>
    public float[]? Extract(Mat bgr, DetectedFace face)
    {
        lock (_sync)
        {
            if (_net == null || _net.Empty())
            {
                _lastError = "识别模型尚未载入。";
                return null;
            }

            try
            {
                var landmarks = new double[10];
                for (var k = 0; k < 10; k++)
                {
                    landmarks[k] = face.Box[4 + k];
                }

                using var transform = GetSimilarityTransformMatrix(landmarks);
                using var aligned = new Mat();
                Cv2.WarpAffine(bgr, aligned, transform, new Size(AlignSize, AlignSize),
                    InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(0, 0, 0));
                if (aligned.Empty())
                {
                    return null;
                }

                // 预处理跟着实际载入的文件走，不跟着设置走。
                using var blob = _isArcFace
                    ? Dnn.CvDnn.BlobFromImage(aligned, 1.0 / 127.5, new Size(AlignSize, AlignSize),
                        new Scalar(127.5, 127.5, 127.5), true, false)
                    : Dnn.CvDnn.BlobFromImage(aligned, 1.0, new Size(AlignSize, AlignSize),
                        new Scalar(0, 0, 0), true, false);

                _net.SetInput(blob, "");
                using var output = _net.Forward("");
                if (output.Empty() || output.Total() < 64)
                {
                    return null;
                }

                Mat? reshaped = null;
                var feature = output;
                if (output.Rows != 1)
                {
                    reshaped = output.Reshape(1, 1);
                    feature = reshaped;
                }

                try
                {
                    var length = feature.Cols;
                    var vector = new float[length];
                    for (var j = 0; j < length; j++)
                    {
                        vector[j] = feature.At<float>(0, j);
                    }

                    _featureLength = length;
                    _lastError = "";
                    return vector;
                }
                finally
                {
                    reshaped?.Dispose();
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "提取人脸特征失败，已跳过该脸");
                _lastError = "提取人脸特征失败：" + e.Message;
                return null;
            }
        }
    }

    /// <summary>
    /// 计算 5 点关键点到标准正脸的相似变换矩阵（Umeyama 算法）。
    /// 移植自 OpenCV <c>face_recognize.cpp</c> 的 <c>getSimilarityTransformMatrix</c>。
    /// </summary>
    private static Mat GetSimilarityTransformMatrix(double[] src)
    {
        double avgX = 0, avgY = 0;
        for (var i = 0; i < 5; i++)
        {
            avgX += src[i * 2];
            avgY += src[i * 2 + 1];
        }

        double[] srcMean = { avgX / 5.0, avgY / 5.0 };

        var srcDemean = new double[5, 2];
        var dstDemean = new double[5, 2];
        for (var j = 0; j < 5; j++)
        {
            srcDemean[j, 0] = src[j * 2] - srcMean[0];
            srcDemean[j, 1] = src[j * 2 + 1] - srcMean[1];
            dstDemean[j, 0] = AlignDst[j, 0] - AlignDstMean[0];
            dstDemean[j, 1] = AlignDst[j, 1] - AlignDstMean[1];
        }

        double a00 = 0, a01 = 0, a10 = 0, a11 = 0;
        for (var i = 0; i < 5; i++)
        {
            a00 += dstDemean[i, 0] * srcDemean[i, 0];
            a01 += dstDemean[i, 0] * srcDemean[i, 1];
            a10 += dstDemean[i, 1] * srcDemean[i, 0];
            a11 += dstDemean[i, 1] * srcDemean[i, 1];
        }

        a00 /= 5;
        a01 /= 5;
        a10 /= 5;
        a11 /= 5;

        var d = new[] { 1.0, 1.0 };
        if (a00 * a11 - a01 * a10 < 0)
        {
            d[1] = -1;
        }

        using var a = new Mat(2, 2, MatType.CV_64FC1);
        a.Set(0, 0, a00);
        a.Set(0, 1, a01);
        a.Set(1, 0, a10);
        a.Set(1, 1, a11);

        using var s = new Mat();
        using var u = new Mat();
        using var vt = new Mat();
        Cv2.SVDecomp(a, s, u, vt, SVD.Flags.None);

        var s0 = s.At<double>(0, 0);
        var s1 = s.At<double>(1, 0);

        var u00 = u.At<double>(0, 0);
        var u01 = u.At<double>(0, 1);
        var u10 = u.At<double>(1, 0);
        var u11 = u.At<double>(1, 1);
        var v00 = vt.At<double>(0, 0);
        var v01 = vt.At<double>(0, 1);
        var v10 = vt.At<double>(1, 0);
        var v11 = vt.At<double>(1, 1);

        var detU = u00 * u11 - u01 * u10;
        var detVt = v00 * v11 - v01 * v10;

        var smax = Math.Max(s0, s1);
        var tol = smax * 2 * float.MinValue;
        var rank = (s0 > tol ? 1 : 0) + (s1 > tol ? 1 : 0);

        double t00, t01, t10, t11;
        if (rank == 1 && detU * detVt > 0)
        {
            t00 = u00 * v00 + u01 * v10;
            t01 = u00 * v01 + u01 * v11;
            t10 = u10 * v00 + u11 * v10;
            t11 = u10 * v01 + u11 * v11;
        }
        else if (rank == 1)
        {
            t00 = u00 * d[0] * v00 - u01 * v10;
            t01 = u00 * d[0] * v01 - u01 * v11;
            t10 = u10 * d[0] * v00 - u11 * v10;
            t11 = u10 * d[0] * v01 - u11 * v11;
        }
        else
        {
            t00 = u00 * d[0] * v00 + u01 * d[1] * v10;
            t01 = u00 * d[0] * v01 + u01 * d[1] * v11;
            t10 = u10 * d[0] * v00 + u11 * d[1] * v10;
            t11 = u10 * d[0] * v01 + u11 * d[1] * v11;
        }

        double var1 = 0, var2 = 0;
        for (var i = 0; i < 5; i++)
        {
            var1 += srcDemean[i, 0] * srcDemean[i, 0];
            var2 += srcDemean[i, 1] * srcDemean[i, 1];
        }

        var1 /= 5;
        var2 /= 5;

        var denom = var1 + var2;
        var scale = denom <= double.Epsilon ? 1.0 : (s0 * d[0] + s1 * d[1]) / denom;

        var ts0 = t00 * srcMean[0] + t01 * srcMean[1];
        var ts1 = t10 * srcMean[0] + t11 * srcMean[1];

        var result = new Mat(2, 3, MatType.CV_64FC1);
        result.Set(0, 0, t00 * scale);
        result.Set(0, 1, t01 * scale);
        result.Set(0, 2, AlignDstMean[0] - scale * ts0);
        result.Set(1, 0, t10 * scale);
        result.Set(1, 1, t11 * scale);
        result.Set(1, 2, AlignDstMean[1] - scale * ts1);
        return result;
    }

    /// <summary>
    /// 余弦相似度。与 OpenCV <c>FaceRecognizerSF::match(FR_COSINE)</c> 的算法一致
    /// （先 L2 归一化再点积）。
    /// </summary>
    public static double Similarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0d;
        }

        double dot = 0d, normA = 0d, normB = 0d;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA <= double.Epsilon || normB <= double.Epsilon)
        {
            return 0d;
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _net?.Dispose();
            _net = null;
        }
    }
}
