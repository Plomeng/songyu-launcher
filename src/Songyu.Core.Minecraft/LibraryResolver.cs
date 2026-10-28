namespace Songyu.Core.Minecraft;

/// <summary>一个库真正要下载的东西。</summary>
public sealed record LibraryArtifact(string RelativePath, string Url, string? Sha1, long Size);

/// <summary>
/// 解析库的下载信息。有两种格式并存：
/// <list type="bullet">
///   <item>Mojang 新版：<c>downloads.artifact</c> 直接给出路径、URL 与 SHA1；</item>
///   <item>Fabric 等：只给 <c>name</c> + <c>url</c>（maven 仓库根），需要自己拼路径。</item>
/// </list>
/// 只认第一种会把加载器本体漏掉，运行时报 ClassNotFoundException。
/// </summary>
public static class LibraryResolver
{
    public static LibraryArtifact? Resolve(Library library)
    {
        if (library.Downloads?.Artifact is { Url.Length: > 0 } artifact)
        {
            var relative = artifact.Path ?? MavenCoordinate.ToPath(library.Name);
            return new LibraryArtifact(relative, artifact.Url, artifact.Sha1, artifact.Size);
        }

        if (!string.IsNullOrEmpty(library.Url) && !string.IsNullOrEmpty(library.Name))
        {
            var relative = MavenCoordinate.ToPath(library.Name);
            var root = library.Url.TrimEnd('/');
            return new LibraryArtifact(relative, $"{root}/{relative}", null, 0);
        }

        return null;
    }
}
