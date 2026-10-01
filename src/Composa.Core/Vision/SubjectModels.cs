// Ported from Lolly (github.com/lolly-tools/lolly, packages/node-shell/src/ml/matte-models.ts and scripts/fetch-matte-models.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
using System.Security.Cryptography;

namespace Composa.Vision;

/// <summary>How a model's single-channel output becomes a 0 to 1 matte.</summary>
public enum MaskActivation
{
    /// <summary>The head is already bounded; stretch what it gave to the full range, as rembg does.</summary>
    MinMax,
    /// <summary>The head is a logit; squash it.</summary>
    Sigmoid
}

/// <summary>
/// One on-device segmentation model: where its file is, what the file must hash to, and the tensor contract the
/// runner needs. The normalization and the activation differ per model and a wrong value does not crash, it quietly
/// ruins the matte, so every value here was confirmed against the real graph (see <c>SubjectModelTests</c>).
/// </summary>
public sealed record SubjectModel(
    string Id,
    string Name,
    string File,
    string Sha256,
    long Bytes,
    string Licence,
    string Attribution,
    string Source,
    string Url,
    int InputSize,
    float[] Mean,
    float[] Std,
    MaskActivation Activation,
    string Note)
{
    /// <summary>The file's full path in the models folder, whether or not it is there.</summary>
    public string Path => System.IO.Path.Combine(SubjectModels.Directory, File);

    /// <summary>Whether the file is on this machine. Never reads it; the hash is checked when it is first loaded.</summary>
    public bool IsInstalled => System.IO.File.Exists(Path);

    /// <summary>Whether the file on disk is the one this record names. A damaged or swapped file is unavailable, not a crash.</summary>
    public bool Verify()
    {
        if (!IsInstalled) return false;
        try
        {
            using var stream = System.IO.File.OpenRead(Path);
            if (stream.Length != Bytes) return false;
            return Convert.ToHexStringLower(SHA256.HashData(stream)) == Sha256;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// The models Composa knows. Only weights under a permissive licence (Apache-2.0, MIT or BSD) are listed: BRIA's RMBG
/// models are non-commercial and never ship. Every entry's licence, source and hash is also recorded in
/// <c>packaging/THIRD-PARTY-NOTICES.txt</c>, which travels with the files.
/// </summary>
public static class SubjectModels
{
    private static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];

    /// <summary>U²-Net lite: a saliency net for any subject, 4.6 MB at 320², and the default. Edges are soft, and busy or low-contrast backgrounds can confuse it.</summary>
    public static readonly SubjectModel U2NetP = new(
        Id: "u2netp",
        Name: "Any subject",
        File: "u2netp.onnx",
        Sha256: "309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8",
        Bytes: 4_574_861,
        Licence: "Apache-2.0",
        Attribution: "U²-Net, Copyright (c) 2020 Xuebin Qin et al.",
        Source: "https://github.com/xuebinqin/U-2-Net",
        Url: "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx",
        InputSize: 320,
        Mean: ImageNetMean,
        Std: ImageNetStd,
        Activation: MaskActivation.MinMax,
        Note: "Works on any subject. Edges are soft, and busy or low-contrast backgrounds can confuse it.");

    /// <summary>MODNet: a portrait matting net, 26 MB, run at 512². Follows hair and soft edges; weaker on anything that is not a person.</summary>
    public static readonly SubjectModel ModNet = new(
        Id: "modnet",
        Name: "Person",
        File: "modnet.onnx",
        Sha256: "07c308cf0fc7e6e8b2065a12ed7fc07e1de8febb7dc7839d7b7f15dd66584df9",
        Bytes: 25_888_640,
        Licence: "Apache-2.0",
        Attribution: "MODNet, Copyright (c) 2020 Zhanghan Ke et al.",
        Source: "https://github.com/ZHKKKe/MODNet",
        Url: "https://huggingface.co/Xenova/modnet/resolve/main/onnx/model.onnx",
        InputSize: 512,
        Mean: [0.5f, 0.5f, 0.5f],
        Std: [0.5f, 0.5f, 0.5f],
        Activation: MaskActivation.MinMax,
        Note: "Tuned for people: soft hair and edges. Weaker on anything that is not a person.");

    public static readonly IReadOnlyList<SubjectModel> All = [U2NetP, ModNet];

    /// <summary>
    /// Where the model files are: <c>COMPOSA_MODELS_DIR</c> when set, otherwise the <c>models</c> folder beside the
    /// application, which is where the build puts them. Nothing is ever downloaded at run time.
    /// </summary>
    public static string Directory =>
        Environment.GetEnvironmentVariable("COMPOSA_MODELS_DIR") is { Length: > 0 } dir ? dir : System.IO.Path.Combine(AppContext.BaseDirectory, "models");

    public static SubjectModel? Find(string id) => All.FirstOrDefault(m => m.Id == id);
}
