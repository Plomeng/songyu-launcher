using System.IO;
using fNbt;

namespace Songyu.Services;

/// <summary>游戏内服务器列表里的一条记录。</summary>
public sealed record GameServer(string Name, string Address, bool Hidden)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Address : Name;
}

/// <summary>
/// 读写实例的 <c>servers.dat</c>（GZip 压缩的 NBT），让启动器里的服务器列表
/// 和游戏内「多人游戏」看到的是同一份数据。
/// </summary>
public sealed class ServerListService(MinecraftPaths paths)
{
    private const string ListTagName = "servers";

    public string ServersDatPath(string instanceId)
        => Path.Combine(paths.InstanceDir(instanceId), "servers.dat");

    public async Task<IReadOnlyList<GameServer>> ReadAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var path = ServersDatPath(instanceId);
        if (!File.Exists(path)) { return []; }
        var file = await Task.Run(() =>
        {
            var result = new NbtFile();
            result.LoadFromFile(path, NbtCompression.AutoDetect, null);
            return result;
        }, cancellationToken);
        var list = file.RootTag.Get<NbtList>(ListTagName);

        if (list is null)
        {
            return [];
        }

        return list
            .OfType<NbtCompound>()
            .Select(compound => new GameServer(
                compound.Get<NbtString>("name")?.Value ?? string.Empty,
                compound.Get<NbtString>("ip")?.Value ?? string.Empty,
                (compound.Get<NbtByte>("hidden")?.Value ?? 0) != 0))
            .Where(server => !string.IsNullOrWhiteSpace(server.Address))
            .ToList();
    }

    public async Task SaveAsync(
        string instanceId,
        IReadOnlyList<GameServer> servers,
        CancellationToken cancellationToken = default)
    {
        var path = ServersDatPath(instanceId);
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new NbtFile();
            if (File.Exists(path)) { file.LoadFromFile(path, NbtCompression.AutoDetect, null); }
            var previous = file.RootTag.Get<NbtList>(ListTagName);
            var list = new NbtList(ListTagName, NbtTagType.Compound);

            foreach (var server in servers)
            {
                // 保留已有服务器的图标和游戏附加字段。
                var existing = previous?.OfType<NbtCompound>()
                    .FirstOrDefault(c => c.Get<NbtString>("ip")?.Value == server.Address);
                var compound = existing is null ? new NbtCompound() : (NbtCompound)existing.Clone();
                compound.Remove("name");
                compound.Remove("ip");
                compound.Remove("hidden");
                compound.Add(new NbtString("name", server.Name));
                compound.Add(new NbtString("ip", server.Address));
                compound.Add(new NbtByte("hidden", (byte)(server.Hidden ? 1 : 0)));
                list.Add(compound);
            }

            file.RootTag.Remove(ListTagName);
            file.RootTag.Add(list);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                file.SaveToFile(temporary, NbtCompression.GZip);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        }, cancellationToken);
    }
}
