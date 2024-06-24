﻿using System;
using System.Threading.Tasks;
using System.IO;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using StardewModdingAPI;

namespace TwitchChatIntegration
{
    public class TwitchBot
    {
        const string ip = "irc.chat.twitch.tv";
        const int port = 6697;

        private string username;
        private string password;
        private bool hasAlertedConnected = false;
        private bool shouldRun = true;
        private StreamReader streamReader;
        private StreamWriter streamWriter;
        private TaskCompletionSource<int> connected = new TaskCompletionSource<int>();

        IMonitor monitor;

        public event TwitchChatEventHandler OnMessage = delegate { };
        public delegate void TwitchChatEventHandler(object sender, TwitchChatMessage e);

        public event TwitchChatStatusHandler OnStatus = delegate { };
        public delegate void TwitchChatStatusHandler(bool isError, string locKey, string rawMessage = "");

        public class TwitchChatMessage : EventArgs
        {
            public string Sender { get; set; }
            public string Message { get; set; }
            public string Channel { get; set; }
        }

        public TwitchBot(IMonitor monitor)
        {
            this.monitor = monitor;
        }

        public TwitchBot(string username, string password, IMonitor monitor)
        {
            this.username = username;
            this.password = password;
            this.monitor = monitor;
        }

        public void SetUserPass(string username, string password)
        {
            this.username = username;
            this.password = password;
            this.hasAlertedConnected = false;
        }

        public bool IsInitialized()
        {
            return !string.IsNullOrWhiteSpace(this.username) && !string.IsNullOrWhiteSpace(this.password);
        }

        public async Task Start()
        {
            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(ip, port);
            SslStream sslStream = new SslStream(
                tcpClient.GetStream(),
                false,
                ValidateServerCertificate,
                null
            );
            await sslStream.AuthenticateAsClientAsync(ip);
            streamReader = new StreamReader(sslStream);
            streamWriter = new StreamWriter(sslStream) { NewLine = "\r\n", AutoFlush = true };

            await streamWriter.WriteLineAsync($"PASS {password}");
            await streamWriter.WriteLineAsync($"NICK {username}");
            connected.SetResult(0);

            try
            {
                // Permanent loop waiting for new Twitch messages
                while (this.shouldRun)
                {
                    string line = await streamReader.ReadLineAsync();

                    // If we disconnect between the last read and now, go ahead and disconnect.
                    if (!this.shouldRun)
                        break;

                    string[] split = line.Split(' ');

                    // PING :tmi.twitch.tv
                    // Respond with PONG :tmi.twitch.tv
                    if (line.StartsWith("PING"))
                    {
                        await streamWriter.WriteLineAsync($"PONG {split[1]}");
                    }

                    // Twitch IRC Message Handling
                    if (split.Length > 2)
                    {
                        string IRCMessage = split[1];
                        // Normal message
                        if (IRCMessage == "PRIVMSG")
                        {
                            // Grab name
                            int exclamationPointPosition = split[0].IndexOf("!");
                            string username = split[0].Substring(1, exclamationPointPosition - 1);
                            // Skip the first character, the first colon, then find the next colon
                            int secondColonPosition = line.IndexOf(':', 1);
                            string message = line.Substring(secondColonPosition + 1);
                            string channel = split[2].TrimStart('#');

                            this.OnMessage(this, new TwitchChatMessage
                            {
                                Message = message,
                                Sender = username,
                                Channel = channel
                            });
                        }
                        else if (IRCMessage == "JOIN" || IRCMessage == "ROOMSTATE") // Channel connection established
                        {
                            if (this.hasAlertedConnected)
                                continue;

                            this.OnStatus.Invoke(false, "twitch.status.connected");
                            this.hasAlertedConnected = true;
                        }
                        else if (IRCMessage == "RECONNECT")
                        {
                            this.OnStatus.Invoke(true, "twitch.status.error.reconnect");
                        }
                    }
                }
            }
            catch (NullReferenceException)
            {
                this.monitor.Log($"Encountered an error, most likely caused by invalid Twitch login credentials.", LogLevel.Debug);
                this.OnStatus.Invoke(true, "twitch.status.error.exception");
            }

            // Cleanup our connections...
            tcpClient.Close();
            streamReader.Close();
            streamWriter.Close();
        }

        public async Task JoinChannel(string channel)
        {
            await connected.Task;
            this.OnStatus.Invoke(false, "twitch.status.connecting");
            await streamWriter.WriteLineAsync($"JOIN #{channel}");
        }

        public void Disconnect()
        {
            // This will kill the main processing loop, rendering the tcpClient to close.
            this.shouldRun = false;
        }

        private bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return sslPolicyErrors == SslPolicyErrors.None;
        }
    }
}
