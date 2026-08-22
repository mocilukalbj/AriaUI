namespace AriaUI.Services;

public interface IFileSystemService
{
    void OpenFile(string filePath);
    void OpenDirectory(string directoryPath);
}
