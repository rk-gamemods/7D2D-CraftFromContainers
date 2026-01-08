using System;
using System.IO;

namespace ProxiCraft;

/// <summary>
/// Network packet for synchronizing container lock state in multiplayer.
/// When a player opens or closes a container, this packet is broadcast to
/// other clients so they know not to pull items from that container.
/// Includes timestamp for stale packet detection and latency diagnostics.
/// </summary>
internal class NetPackagePCLock : NetPackage
{
    public int posX;
    public int posY;
    public int posZ;
    public bool unlock;
    public long timestampUtcTicks; // UTC timestamp for latency tracking

    public NetPackagePCLock Setup(Vector3i _pos, bool _unlock)
    {
        posX = _pos.x;
        posY = _pos.y;
        posZ = _pos.z;
        unlock = _unlock;
        timestampUtcTicks = DateTime.UtcNow.Ticks;
        return this;
    }

    public override void read(PooledBinaryReader _br)
    {
        try
        {
            var reader = (BinaryReader)(object)_br;
            posX = reader.ReadInt32();
            posY = reader.ReadInt32();
            posZ = reader.ReadInt32();
            unlock = reader.ReadBoolean();
            timestampUtcTicks = reader.ReadInt64();
        }
        catch (Exception ex)
        {
            // Malformed packet - set to safe defaults
            ProxiCraft.LogDebug($"[Network] Failed to read lock packet: {ex.Message}");
            posX = posY = posZ = int.MinValue;
            unlock = true; // Default to unlock (safer)
            timestampUtcTicks = 0;
        }
    }

    public override void write(PooledBinaryWriter _bw)
    {
        try
        {
            ((NetPackage)this).write(_bw);
            var writer = (BinaryWriter)(object)_bw;
            writer.Write(posX);
            writer.Write(posY);
            writer.Write(posZ);
            writer.Write(unlock);
            writer.Write(timestampUtcTicks);
        }
        catch (Exception ex)
        {
            ProxiCraft.LogDebug($"[Network] Failed to write lock packet: {ex.Message}");
        }
    }

    public override int GetLength()
    {
        return sizeof(int) * 3 + sizeof(bool) + sizeof(long);
    }

    public override void ProcessPackage(World _world, GameManager _callbacks)
    {
        // Only process on clients, not on the server
        if (ProxiCraft.Config?.modEnabled != true)
            return;
            
        if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            return;

        // Validate packet data
        if (posX == int.MinValue)
        {
            ProxiCraft.LogDebug("[Network] Ignoring invalid lock packet");
            return;
        }

        var position = new Vector3i(posX, posY, posZ);
        
        // Calculate and log latency if timestamp is valid
        if (timestampUtcTicks > 0)
        {
            var sentTime = new DateTime(timestampUtcTicks, DateTimeKind.Utc);
            var latencyMs = (DateTime.UtcNow - sentTime).TotalMilliseconds;
            
            // Log if latency is unusually high (>500ms)
            if (latencyMs > 500)
            {
                ProxiCraft.LogWarning($"[Network] High latency detected: Lock packet took {latencyMs:F0}ms (sent {sentTime:HH:mm:ss.fff} UTC)");
            }
            else if (ProxiCraft.Config?.isDebug == true)
            {
                ProxiCraft.LogDebug($"[Network] Lock packet latency: {latencyMs:F0}ms");
            }
        }
        
        // Use ContainerManager's lock methods for last-write-wins ordering and expiration
        if (!unlock)
        {
            ContainerManager.AddLock(position, timestampUtcTicks);
        }
        else
        {
            ContainerManager.RemoveLock(position, timestampUtcTicks);
        }
    }
}
