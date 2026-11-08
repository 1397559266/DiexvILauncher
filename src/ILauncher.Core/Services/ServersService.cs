using System.IO.Compression;
using System.Text;

namespace ILauncher.Core.Services;

public sealed record ServerEntry(string Name, string Ip, string? IconBase64)
{
    public string Host
    {
        get
        {
            var value = Ip;
            var colon = value.LastIndexOf(':');
            return colon > 0 && value.Count(c => c == ':') == 1 ? value[..colon] : value;
        }
    }

    public int Port
    {
        get
        {
            var value = Ip;
            var colon = value.LastIndexOf(':');
            return colon > 0 && int.TryParse(value[(colon + 1)..], out var port) ? port : 25565;
        }
    }
}

/// <summary>Read/write support for Minecraft's servers.dat (NBT).</summary>
public sealed class ServersService
{
    public List<ServerEntry> Load(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, "servers.dat");
        if (!File.Exists(path))
        {
            return new List<ServerEntry>();
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);
            Stream input = stream;
            if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                input = new GZipStream(stream, CompressionMode.Decompress);
            }

            using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
            var root = ReadTag(reader);
            if (root is not NbtCompound compound ||
                !compound.Values.TryGetValue("servers", out var serversValue) ||
                serversValue is not NbtList list)
            {
                return new List<ServerEntry>();
            }

            var result = new List<ServerEntry>();
            foreach (var item in list.Items)
            {
                if (item is not NbtCompound server)
                {
                    continue;
                }

                var name = server.Values.TryGetValue("name", out var nameValue) ? nameValue as string ?? "" : "";
                var ip = server.Values.TryGetValue("ip", out var ipValue) ? ipValue as string ?? "" : "";
                var icon = server.Values.TryGetValue("icon", out var iconValue) ? iconValue as string : null;
                if (!string.IsNullOrWhiteSpace(ip))
                {
                    result.Add(new ServerEntry(name, ip, icon));
                }
            }

            return result;
        }
        catch
        {
            return new List<ServerEntry>();
        }
    }

    public void Save(string gameDirectory, IEnumerable<ServerEntry> servers)
    {
        Directory.CreateDirectory(gameDirectory);
        var path = Path.Combine(gameDirectory, "servers.dat");
        var temp = path + ".tmp";
        var items = servers.ToList();

        using (var stream = File.Create(temp))
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write((byte)10);
            WriteString(writer, "");
            writer.Write((byte)9);
            WriteString(writer, "servers");
            writer.Write((byte)10);
            WriteInt(writer, items.Count);
            foreach (var server in items)
            {
                writer.Write((byte)8);
                WriteString(writer, "name");
                WriteString(writer, server.Name);
                writer.Write((byte)8);
                WriteString(writer, "ip");
                WriteString(writer, server.Ip);
                if (!string.IsNullOrWhiteSpace(server.IconBase64))
                {
                    writer.Write((byte)8);
                    WriteString(writer, "icon");
                    WriteString(writer, server.IconBase64);
                }

                writer.Write((byte)1);
                WriteString(writer, "acceptTextures");
                writer.Write((byte)1);
                writer.Write((byte)0);
            }

            writer.Write((byte)0);
        }

        if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((byte)(bytes.Length >> 8));
        writer.Write((byte)(bytes.Length & 0xFF));
        writer.Write(bytes);
    }

    private static void WriteInt(BinaryWriter writer, int value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static object? ReadTag(BinaryReader reader)
    {
        var type = reader.ReadByte();
        if (type == 0)
        {
            return null;
        }

        _ = ReadString(reader);
        return ReadPayload(reader, type);
    }

    private static object? ReadPayload(BinaryReader reader, byte type) => type switch
    {
        1 => reader.ReadByte(),
        2 => ReadInt16BigEndian(reader),
        3 => ReadInt32BigEndian(reader),
        4 => ReadInt64BigEndian(reader),
        5 => BitConverter.Int32BitsToSingle(ReadInt32BigEndian(reader)),
        6 => BitConverter.Int64BitsToDouble(ReadInt64BigEndian(reader)),
        7 => reader.ReadBytes(ReadInt32BigEndian(reader)),
        8 => ReadString(reader),
        9 => ReadList(reader),
        10 => ReadCompound(reader),
        11 => ReadIntArray(reader),
        12 => ReadLongArray(reader),
        _ => null
    };

    private static NbtList ReadList(BinaryReader reader)
    {
        var itemType = reader.ReadByte();
        var count = ReadInt32BigEndian(reader);
        var items = new List<object?>(Math.Max(0, Math.Min(count, 4096)));
        for (var i = 0; i < count; i++)
        {
            items.Add(ReadPayload(reader, itemType));
        }

        return new NbtList(items);
    }

    private static NbtCompound ReadCompound(BinaryReader reader)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (true)
        {
            var type = reader.ReadByte();
            if (type == 0)
            {
                break;
            }

            var name = ReadString(reader);
            values[name] = ReadPayload(reader, type);
        }

        return new NbtCompound(values);
    }

    private static object ReadIntArray(BinaryReader reader)
    {
        var count = ReadInt32BigEndian(reader);
        var values = new int[Math.Max(0, count)];
        for (var i = 0; i < count; i++)
        {
            values[i] = ReadInt32BigEndian(reader);
        }

        return values;
    }

    private static object ReadLongArray(BinaryReader reader)
    {
        var count = ReadInt32BigEndian(reader);
        var values = new long[Math.Max(0, count)];
        for (var i = 0; i < count; i++)
        {
            values[i] = ReadInt64BigEndian(reader);
        }

        return values;
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = (reader.ReadByte() << 8) | reader.ReadByte();
        var bytes = reader.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static short ReadInt16BigEndian(BinaryReader reader)
        => (short)((reader.ReadByte() << 8) | reader.ReadByte());

    private static int ReadInt32BigEndian(BinaryReader reader)
        => (reader.ReadByte() << 24) | (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();

    private static long ReadInt64BigEndian(BinaryReader reader)
    {
        long value = 0;
        for (var i = 0; i < 8; i++)
        {
            value = (value << 8) | reader.ReadByte();
        }

        return value;
    }

    private sealed record NbtCompound(Dictionary<string, object?> Values);
    private sealed record NbtList(List<object?> Items);
}
