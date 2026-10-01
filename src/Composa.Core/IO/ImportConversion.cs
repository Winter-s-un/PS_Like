namespace Composa.IO;

/// <summary>
/// One thing that had to change on the way in from another editor's file, reported per layer before anything is
/// applied. Photoshop and GIMP files both report through it; a note about the file as a whole carries the file's name.
/// </summary>
public sealed record ImportConversion(string LayerName, string Message);
