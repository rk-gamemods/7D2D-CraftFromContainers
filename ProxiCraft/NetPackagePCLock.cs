using System.IO;

namespace ProxiCraft;

/// <summary>
/// Network packet for synchronizing container lock state in multiplayer.
/// When a player opens or closes a container, this packet is broadcast to
/// other clients so they know not to pull items from that container.
/// </summary>
internal class NetPackagePCLock : NetPackage
{
    public int posX;
    public int posY;
    public int posZ;
    public bool unlock;

    public NetPackagePCLock Setup(Vector3i _pos, bool _unlock)
    {
        posX = _pos.x;
        posY = _pos.y;
        posZ = _pos.z;
        unlock = _unlock;
        return this;
    }

    public override void read(PooledBinaryReader _br)
    {
        posX = ((BinaryReader)(object)_br).ReadInt32();
        posY = ((BinaryReader)(object)_br).ReadInt32();
        posZ = ((BinaryReader)(object)_br).ReadInt32();
        unlock = ((BinaryReader)(object)_br).ReadBoolean();
    }

    public override void write(PooledBinaryWriter _bw)
    {
        ((NetPackage)this).write(_bw);
        ((BinaryWriter)(object)_bw).Write(posX);
        ((BinaryWriter)(object)_bw).Write(posY);
        ((BinaryWriter)(object)_bw).Write(posZ);
        ((BinaryWriter)(object)_bw).Write(unlock);
    }

    public override int GetLength()
    {
        return sizeof(int) * 3 + sizeof(bool);
    }

    public override void ProcessPackage(World _world, GameManager _callbacks)
    {
        // Only process on clients, not on the server
        if (ProxiCraft.Config?.modEnabled != true)
            return;
            
        if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            return;

        var position = new Vector3i(posX, posY, posZ);
        
        if (!unlock)
        {
            ProxiCraft.LogDebug($"Received locked message for {position}");
            ContainerManager.LockedList.Add(position);
        }
        else
        {
            ProxiCraft.LogDebug($"Received unlocked message for {position}");
            ContainerManager.LockedList.Remove(position);
        }
    }
}
