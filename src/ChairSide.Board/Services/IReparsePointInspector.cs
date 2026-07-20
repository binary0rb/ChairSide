namespace ChairSide.Board.Services;

public interface IReparsePointInspector
{
    FileAttributes? GetAttributesIfExists(string path);
}

public sealed class FileSystemReparsePointInspector : IReparsePointInspector
{
    public FileAttributes? GetAttributesIfExists(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
