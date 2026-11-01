namespace OpenGZH.Formats.W3d;

/// <summary>A mounted package that supplies W3D models some other way than .w3d files (Kane's Wrath's compiled art).</summary>
public interface IW3dProvider
{
    /// <summary>The model, or null if this package does not have it.</summary>
    W3dFile? GetW3d(string name);
}
