using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using Craft.Net.Server.Events;
using Craft.Net.Server.Packets;
using Craft.Net.Server.Worlds;
using Craft.Net.Server.Worlds.Entities;

namespace Craft.Net.Server
{
    /// <summary>
    /// A Minecraft server.
    /// </summary>
    public class MinecraftServer
    {
        #region Public Fields

        public const int ProtocolVersion = 40;

        public List<MinecraftClient> Clients;
        public int DefaultWorldIndex;
        public bool EncryptionEnabled;
        public List<ILogProvider> LogProviders;
        public byte MaxPlayers;
        public string MotD;
        public bool OnlineMode;
        public List<World> Worlds;

        public event EventHandler<ChatMessageEventArgs> OnChatMessage;

        #endregion

        #region Private Fields

        internal static Random Random;
        internal RSACryptoServiceProvider CryptoServiceProvider;
        internal Dictionary<string, PluginChannel> PluginChannels;
        private AutoResetEvent SendQueueReset;
        private Thread SendQueueThread;
        internal RSAParameters ServerKey;
        private Timer UpdatePlayerListTimer;
        private Socket socket;

        #endregion

        #region Public Properties

        public World DefaultWorld
        {
            get { return Worlds[DefaultWorldIndex]; }
        }

        #endregion

        #region Constructor

        public MinecraftServer(IPEndPoint EndPoint)
        {
            Clients = new List<MinecraftClient>();
            MaxPlayers = 25;
            MotD = "Craft.Net Server";
            OnlineMode = EncryptionEnabled = true;
            Random = new Random();
            DefaultWorldIndex = 0;
            Worlds = new List<World>();
            LogProviders = new List<ILogProvider>();
            PluginChannels = new Dictionary<string, PluginChannel>();

            socket = new Socket(AddressFamily.InterNetwork,
                                SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(EndPoint);
        }

        #endregion

        #region Public Methods

        public void Start()
        {
            if (Worlds.Count == 0)
            {
                Log("Unable to start server with no worlds loaded.");
                throw new InvalidOperationException("Unable to start server with no worlds loaded.");
            }

            Log("Starting Craft.Net server...");

            CryptoServiceProvider = new RSACryptoServiceProvider(1024);
            ServerKey = CryptoServiceProvider.ExportParameters(true);

            socket.Listen(10);
            SendQueueReset = new AutoResetEvent(false);
            SendQueueThread = new Thread(SendQueueWorker);
            SendQueueThread.Start();
            socket.BeginAccept(AcceptConnectionAsync, null);

            UpdatePlayerListTimer = new Timer(UpdatePlayerList, null, 60000, 60000);

            Log("Server started.");
        }

        public void Stop()
        {
            Log("Stopping server...");
            if (SendQueueThread != null)
            {
                SendQueueThread.Abort();
                SendQueueThread = null;
            }
            if (socket != null)
            {
                if (socket.Connected)
                    socket.Shutdown(SocketShutdown.Both);
                socket = null;
            }
            UpdatePlayerListTimer.Dispose();
            Log("Server stopped.");
        }

        public void ProcessSendQueue()
        {
            if (SendQueueReset != null)
                SendQueueReset.Set();
        }

        public void AddLogProvider(ILogProvider LogProvider)
        {
            LogProviders.Add(LogProvider);
        }

        public void Log(string Text)
        {
            Log(Text, LogImportance.High);
        }

        public void Log(string Text, LogImportance LogLevel)
        {
            foreach (ILogProvider provider in LogProviders)
                provider.Log(Text, LogLevel);
        }

        public void AddWorld(World World)
        {
            World.EntityManager.Server = this;
            World.OnBlockChanged += HandleOnBlockChanged;
            Worlds.Add(World);
        }

        private void HandleOnBlockChanged(object sender, BlockChangedEventArgs e)
        {
            foreach (MinecraftClient client in GetClientsInWorld(e.World))
                client.SendPacket(new BlockChangePacket(e.Position, e.Value));
            ProcessSendQueue();
        }

        public World GetClientWorld(MinecraftClient Client)
        {
            foreach (World world in Worlds)
            {
                if (world.EntityManager.Entities.Contains(Client.Entity))
                    return world;
            }
            return null;
        }

        public MinecraftClient[] GetClientsInWorld(World world)
        {
            var clients = new List<MinecraftClient>();
            foreach (MinecraftClient client in Clients)
            {
                if (world.EntityManager.Entities.Contains(client.Entity))
                    clients.Add(client);
            }
            return clients.ToArray();
        }

        public void SendChat(string message)
        {
            for (int i = 0; i < Clients.Count; i++)
                Clients[i].SendPacket(new ChatMessagePacket(message));
            ProcessSendQueue();
        }

        public void RegisterPluginChannel(PluginChannel channel)
        {
            PluginChannels.Add(channel.Channel, channel);
            channel.ChannelRegistered(this);
        }

        #endregion

        #region Private Methods

        private void SendQueueWorker()
        {
            while (true)
            {
                SendQueueReset.Reset();
                SendQueueReset.WaitOne();
                if (Clients.Count != 0)
                {
                    for (int i = 0; i < Clients.Count; i++)
                    {
                        while (Clients[i].SendQueue.Count != 0)
                        {
                            Packet packet = Clients[i].SendQueue.Dequeue();
                            Log("[SERVER->CLIENT] " + Clients[i].Socket.RemoteEndPoint,
                                LogImportance.Low);
                            Log(packet.ToString(), LogImportance.Low);
                            try
                            {
                                packet.SendPacket(this, Clients[i]);
                                packet.FirePacketSent();
                            }
                            catch
                            {
                                // Occasionally, the client will disconnect while
                                // processing the packet to be sent, which causes
                                // a fatal exception.
                                lock (Clients)
                                {
                                    if (i < Clients.Count)
                                    {
                                        Clients[i].IsDisconnected = true;
                                        if (Clients[i].Socket.Connected)
                                            Clients[i].Socket.BeginDisconnect(false, null, null);
                                    }
                                    i--;
                                }
                                break;
                            }
                        }
                    }
                }
                Thread.Sleep(1);
            }
        }

        private void AcceptConnectionAsync(IAsyncResult result)
        {
            Socket connection = socket.EndAccept(result);
            var client = new MinecraftClient(connection, this);
            Clients.Add(client);
            client.Socket.SendTimeout = 5000;
            client.Socket.BeginReceive(client.RecieveBuffer, client.RecieveBufferIndex,
                                       client.RecieveBuffer.Length,
                                       SocketFlags.None, SocketRecieveAsync, client);
            socket.BeginAccept(AcceptConnectionAsync, null);
        }

        private void SocketRecieveAsync(IAsyncResult result)
        {
            var client = (MinecraftClient) result.AsyncState;
            SocketError error;
            int length = client.Socket.EndReceive(result, out error) + client.RecieveBufferIndex;
            if (error != SocketError.Success || !client.Socket.Connected || length == client.RecieveBufferIndex)
            {
                if (error != SocketError.Success)
                    Log("Socket error: " + error);
                client.IsDisconnected = true;
            }
            else
            {
                try
                {
                    IEnumerable<Packet> packets = PacketReader.TryReadPackets(ref client, length);
                    foreach (Packet packet in packets)
                        packet.HandlePacket(this, ref client);

                    client.Socket.BeginReceive(client.RecieveBuffer, client.RecieveBufferIndex,
                                               client.RecieveBuffer.Length - client.RecieveBufferIndex,
                                               SocketFlags.None, SocketRecieveAsync, client);
                }
                catch (InvalidOperationException e)
                {
                    client.IsDisconnected = true;
                    Log("Disconnected client with protocol error. " + e.Message);
                }
                catch (NotImplementedException)
                {
                    client.IsDisconnected = true;
                    Log("Disconnected client using unsupported features.");
                }
            }
            if (client.IsDisconnected)
            {
                if (client.Socket.Connected)
                    client.Socket.BeginDisconnect(false, null, null);
                if (client.KeepAliveTimer != null)
                    client.KeepAliveTimer.Dispose();
                if (client.IsLoggedIn)
                {
                    foreach (MinecraftClient remainingClient in Clients)
                    {
                        if (remainingClient.IsLoggedIn)
                        {
                            remainingClient.SendPacket(new PlayerListItemPacket(
                                                           client.Username, false, 0));
                        }
                    }
                }
                lock (Clients)
                {
                    Clients.Remove(client);
                }
                ProcessSendQueue();
            }
        }

        public void UpdatePlayerList(object unused)
        {
            if (Clients.Count != 0)
            {
                for (int i = 0; i < Clients.Count; i++)
                {
                    foreach (MinecraftClient client in Clients)
                        Clients[i].SendPacket(new PlayerListItemPacket(
                                                  client.Username, true, client.Ping));
                }
            }
            ProcessSendQueue();
        }

        #endregion

        #region Internal Methods

        internal void FireOnChatMessage(ChatMessageEventArgs e)
        {
            if (OnChatMessage != null)
                OnChatMessage(this, e);
        }

        internal void LogInPlayer(MinecraftClient Client)
        {
            Log(Client.Username + " logged in.");
            Client.IsLoggedIn = true;
            // Spawn player
            Client.Entity = new PlayerEntity(Client);
            Client.Entity.Position = DefaultWorld.SpawnPoint;
            Client.Entity.Position += new Vector3(0, PlayerEntity.Height, 0);
            DefaultWorld.EntityManager.SpawnEntity(Client.Entity);
            Client.SendPacket(new LoginPacket(Client.Entity.Id,
                                              DefaultWorld.LevelType, DefaultWorld.GameMode,
                                              Client.Entity.Dimension, DefaultWorld.Difficulty,
                                              MaxPlayers));

            // Send initial chunks
            Client.UpdateChunks(true);
            MinecraftClient client = Client;
            Client.SendQueue.Last().OnPacketSent += (sender, e) => { client.ReadyToSpawn = true; };
            Client.SendPacket(new PlayerPositionAndLookPacket(
                                  Client.Entity.Position, Client.Entity.Yaw, Client.Entity.Pitch, true));

            UpdatePlayerList(null); // Should also process send queue
        }

        #endregion
    }
}