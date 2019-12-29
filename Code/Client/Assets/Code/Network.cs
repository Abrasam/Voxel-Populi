﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Concurrent;
using System;

public class ServerRejectedException : Exception {
    public ServerRejectedException(string msg) : base(msg) {}
}

public class ChunkThread {

    private readonly string address;
    private readonly int port;
    private readonly int[] chunkCoord;

    private Socket socket;
    private Thread recvThread;
    private Thread sendThread;

    public readonly BlockingCollection<Packet> recv;
    private readonly BlockingCollection<Packet> send;
    
    public ChunkThread(string address, int port, int chunkX, int chunkY) {
        this.address = address;
        this.port = port;
        this.chunkCoord = new int[2] {chunkX, chunkY};
        recv = new BlockingCollection<Packet>(new ConcurrentQueue<Packet>());
        send = new BlockingCollection<Packet>(new ConcurrentQueue<Packet>());

        IPEndPoint ipe = new IPEndPoint(IPAddress.Parse(address), port);

        socket = new Socket(ipe.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        socket.ReceiveTimeout = 1000;

        socket.Connect(ipe);

        socket.Send(System.Text.Encoding.UTF8.GetBytes("{\"type\": \"connect\", \"chunk\": [" + chunkCoord[0] + "," + chunkCoord[1] + "]}"));

        byte[] ok = new byte[2];

        socket.Receive(ok);

        socket.ReceiveTimeout = 0;

        if (System.Text.Encoding.UTF8.GetString(ok) != "ok") {
            throw new ServerRejectedException("Server did not accept connection.");
        }

        recvThread = new Thread(RecvLoop);
        recvThread.IsBackground = true;
        recvThread.Start();
        sendThread = new Thread(SendLoop);
        sendThread.IsBackground = true;
        sendThread.Start();
    }

    private void RecvLoop() {
        try {
            byte newLine = System.Text.Encoding.UTF8.GetBytes("\n")[0];
            while (true) {
                byte[] buf = new byte[1];
                List<byte> msg = new List<byte>();
                socket.Receive(buf);
                while (buf[0] != newLine) {
                    msg.Add(buf[0]);
                    socket.Receive(buf);
                }
                byte[] data = msg.ToArray();
                recv.Add(JsonUtility.FromJson<Packet>(System.Text.Encoding.UTF8.GetString(data)));
            }
        } catch (SocketException) {
            return;
        } catch (ObjectDisposedException) {
            return;
        }
    }

    private void SendLoop() {
        try {
            while (true) {
                Packet p = send.Take();
                //Debug.Log("Packet outgoing!");
                string json = JsonUtility.ToJson(p) + "\n";
                byte[] toSend = System.Text.Encoding.UTF8.GetBytes(json);
                socket.Send(toSend);
            }
        } catch (SocketException) {
            return;
        } catch (ObjectDisposedException) {
            return;
        }
    }

    public void Send(Packet p) {
        send.Add(p);
    }

    public Vector2 GetChunkCoord() {
        return new Vector2(chunkCoord[0], chunkCoord[1]);
    }

    public void Abort() {
        socket.Close();
    }
}

public class NetworkThread {

    private World world;
    private ConcurrentQueue<Update> incomingUpdates;
    private ConcurrentQueue<Update> outgoingUpdates;
    private string bootstrapAddress;
    private int bootstrapPort;
    private Socket socket;
    private List<ChunkThread> servers = new List<ChunkThread>();

    private Thread eventThread;
    private ChunkThread current;

    public NetworkThread(World world, ConcurrentQueue<Update> incoming, ConcurrentQueue<Update> outgoing, string bootstrapAddress, int bootstrapPort) {
        this.world = world;
        this.incomingUpdates = incoming;
        this.outgoingUpdates = outgoing;
        this.bootstrapAddress = bootstrapAddress;
        this.bootstrapPort = bootstrapPort;

        IPEndPoint ipe = new IPEndPoint(IPAddress.Parse(bootstrapAddress), bootstrapPort);

        socket = new Socket(ipe.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        socket.ReceiveTimeout = 1000;

        socket.Connect(ipe);

        socket.Send(System.Text.Encoding.UTF8.GetBytes("{\"type\": \"dht\"}"));

        byte[] ok = new byte[2];

        socket.Receive(ok);

        socket.ReceiveTimeout = 0;

        if (System.Text.Encoding.UTF8.GetString(ok) != "ok") {
            throw new ServerRejectedException("Server did not accept connection.");
        }

        eventThread = new Thread(EventHandler);
        eventThread.IsBackground = true;
        eventThread.Start();
    }

    private void UpdateChunks(Vector3 pos) {
        int chunkX = Mathf.FloorToInt(pos.x / Data.ChunkSize);
        int chunkY = Mathf.FloorToInt(pos.z / Data.ChunkSize);
        List<ChunkThread> rem = new List<ChunkThread>();
        foreach (ChunkThread ct in servers) {
            Vector2 chunk = ct.GetChunkCoord();
            if (!(Math.Abs(chunk.x - chunkX) < 2 && Math.Abs(chunk.y - chunkY) < 2)) {
                rem.Add(ct);
            }
        }
        foreach (ChunkThread r in rem) {
            servers.Remove(r);
            r.Abort();
            incomingUpdates.Enqueue(new Update(UpdateType.UNLOAD_CHUNK, r.GetChunkCoord()));
        }
        for (int i = -1; i < 2; i++) {
            for (int j = -1; j < 2; j++) {
                bool found = false;
                foreach (ChunkThread ct in servers) {
                    Vector2 chunk = ct.GetChunkCoord();
                    if (chunkX + i == chunk.x && chunkY + j == chunk.y) {
                        found = true;
                        break;
                    }
                }
                if (!found) {
                    socket.Send(System.Text.Encoding.UTF8.GetBytes("[" + (chunkX + i) + "," + (chunkY + j) + "]"));
                    byte[] buf = new byte[1];
                    List<byte> msg = new List<byte>();
                    socket.Receive(buf);
                    while (buf[0] != (byte)'\n') {
                        msg.Add(buf[0]);
                        socket.Receive(buf);
                    }
                    byte[] data = msg.ToArray();
                    Address addr = JsonUtility.FromJson<Address>(System.Text.Encoding.UTF8.GetString(data));
                    ChunkThread ct = new ChunkThread(addr.ip, addr.port, chunkX + i, chunkY + j);
                    servers.Add(ct);
                }
            }
        }
        if (current == null || !current.GetChunkCoord().Equals(new Vector2(chunkX, chunkY))) {
            foreach (ChunkThread ct in servers) {
                if (ct.GetChunkCoord().Equals(new Vector2(chunkX, chunkY))) {
                    if (current != null) {
                        current.Send(new Packet((int)PacketType.PLAYER_DEREGISTER, new float[] { }));
                    }
                    current = ct;
                    current.Send(new Packet((int)PacketType.PLAYER_REGISTER, new float[] { }));
                    break;
                }
            }
        }
    }

    private void EventHandler() {
        while (true) {
            int localUpdates = outgoingUpdates.Count;
            for (int i = 0; i < localUpdates; i++) {
                Update u;
                if (outgoingUpdates.TryDequeue(out u)) {
                    switch(u.type) {
                        case UpdateType.PLAYER_MOVE:
                            Vector3 pos = (Vector3)u.arg;
                            UpdateChunks(pos);
                            current.Send(new Packet((int)PacketType.PLAYER_MOVE, new float[] { pos.x, pos.y, pos.z }));
                            break;
                        default:
                            break;
                    }
                }
            }
            foreach (ChunkThread s in servers) {
                int cnt = s.recv.Count;
                for (int i = 0; i < cnt; i++) {
                    Packet p = s.recv.Take();
                    //Debug.Log("Received packet");
                    switch (p.type) {
                        case (int)PacketType.CHUNK_DATA:
                            byte[,,] chunkData = new byte[Data.ChunkSize, Data.ChunkSize, Data.ChunkSize];
                            for (int x = 0; x < Data.ChunkSize; x++) {
                                for (int y = 0; y < Data.ChunkSize; y++) {
                                    for (int z = 0; z < Data.ChunkSize; z++) {
                                        chunkData[x, y, z] = (byte)p.args[2 + Data.ChunkSize * Data.ChunkSize * x + Data.ChunkSize * y + z];
                                    }
                                }
                            }
                            incomingUpdates.Enqueue(new Update(UpdateType.LOAD_CHUNK, new Chunk(new Vector2(p.args[0], p.args[1]), chunkData)));
                            break;
                        case (int)PacketType.PLAYER_MOVE:
                            incomingUpdates.Enqueue(new Update(UpdateType.PLAYER_MOVE, new Vector3(p.args[0], p.args[1], p.args[2])));
                            break;
                        default:
                            break;
                    }
                }
            }
            Thread.Sleep((int)(Data.TickLength * 1000));
        }
    }

    public void Abort() {
        eventThread.Abort();
        foreach (ChunkThread ct in servers) {
            ct.Abort();
        }
    }

    [Serializable]
    private class Address {
        public string ip;
        public int port;
    }
}

[Serializable]
public class Packet {
    public int type;
    public float[] args;
    
    public Packet(int type, float[] args) {
        this.type = type;
        this.args = args;
    }
}

public enum PacketType {
    FIND_CHUNK = 0,
    PLAYER_REGISTER = 1,
    PLAYER_DEREGISTER = 2,
    PLAYER_MOVE = 3,
    FIND_PLAYER = 4,
    CHUNK_DATA = 5
}