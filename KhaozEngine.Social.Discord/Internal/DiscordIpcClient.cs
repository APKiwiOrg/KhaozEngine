using System;
using System.Collections.Generic;
using KhaozEngine.Social;

namespace KhaozEngine.Social.Discord.Internal;

/// <summary>
/// Speaks the Discord IPC protocol over an <see cref="IDiscordIpcTransport"/>: handshake, SET_ACTIVITY,
/// SUBSCRIBE to join events, and a non-blocking dispatch pump. Every socket operation is wrapped so a
/// failure flips the client to disconnected rather than throwing. Pure protocol logic; the real socket
/// lives in <see cref="NamedPipeDiscordTransport"/>.
/// </summary>
internal sealed class DiscordIpcClient : IDisposable
{
    private readonly IDiscordIpcTransport transport;
    private readonly int pid;
    private readonly List<byte> readBuffer = new();
    private int nonce;

    public DiscordIpcClient(IDiscordIpcTransport transport, int? pid = null)
    {
        this.transport = transport;
        this.pid = pid ?? System.Environment.ProcessId;
    }

    public bool IsConnected { get; private set; }
    public SocialUser? LocalUser { get; private set; }

    public event Action<string>? JoinSecretReceived;
    public event Action<SocialUser>? JoinRequestUserReceived;

    public bool TryConnect(string clientId)
    {
        try
        {
            if (!transport.TryConnect())
            {
                return false;
            }

            WriteFrame(DiscordIpcOpcode.Handshake, DiscordIpcPayloads.Handshake(clientId));
            WriteFrame(DiscordIpcOpcode.Frame, DiscordIpcPayloads.Subscribe("ACTIVITY_JOIN", NextNonce()));
            WriteFrame(DiscordIpcOpcode.Frame, DiscordIpcPayloads.Subscribe("ACTIVITY_JOIN_REQUEST", NextNonce()));
            IsConnected = true;
            return true;
        }
        catch (Exception)
        {
            Disconnect();
            return false;
        }
    }

    public void SetActivity(in RichPresence presence)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            WriteFrame(DiscordIpcOpcode.Frame, DiscordIpcPayloads.SetActivity(pid, presence, NextNonce()));
        }
        catch (Exception)
        {
            Disconnect();
        }
    }

    public void ClearActivity()
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            // Clear via a null activity (an empty activity object does not reliably clear).
            WriteFrame(DiscordIpcOpcode.Frame, DiscordIpcPayloads.ClearActivity(pid, NextNonce()));
        }
        catch (Exception)
        {
            Disconnect();
        }
    }

    public void Pump()
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            DrainReads();
            ProcessFrames();
        }
        catch (Exception)
        {
            Disconnect();
        }
    }

    private void DrainReads()
    {
        Span<byte> chunk = stackalloc byte[4096];
        int read;
        while ((read = transport.Read(chunk)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                readBuffer.Add(chunk[i]);
            }
        }
    }

    private void ProcessFrames()
    {
        while (DiscordIpcCodec.TryDecodeFrame(readBuffer.ToArray(), out DiscordIpcOpcode op, out string json, out int consumed))
        {
            readBuffer.RemoveRange(0, consumed);
            HandleFrame(op, json);
        }
    }

    private void HandleFrame(DiscordIpcOpcode op, string json)
    {
        if (op == DiscordIpcOpcode.Close)
        {
            Disconnect();
            return;
        }

        if (op == DiscordIpcOpcode.Ping)
        {
            WriteFrame(DiscordIpcOpcode.Pong, json);
            return;
        }

        if (op != DiscordIpcOpcode.Frame)
        {
            return;
        }

        if (!DiscordIpcPayloads.TryParseDispatch(json, out string evt, out string data))
        {
            return;
        }

        switch (evt)
        {
            case "READY":
                if (DiscordIpcPayloads.TryParseReadyUser(json, out SocialUser user))
                {
                    LocalUser = user;
                }

                break;
            case "ACTIVITY_JOIN":
                if (DiscordIpcPayloads.TryParseJoinSecret(data, out string secret))
                {
                    JoinSecretReceived?.Invoke(secret);
                }

                break;
            case "ACTIVITY_JOIN_REQUEST":
                if (DiscordIpcPayloads.TryParseJoinRequestUser(data, out SocialUser requester))
                {
                    JoinRequestUserReceived?.Invoke(requester);
                }

                break;
        }
    }

    private void WriteFrame(DiscordIpcOpcode op, string json) => transport.Write(DiscordIpcCodec.EncodeFrame(op, json));

    private string NextNonce() => (++nonce).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void Disconnect()
    {
        IsConnected = false;
        LocalUser = null;
    }

    public void Dispose()
    {
        Disconnect();
        transport.Dispose();
    }
}
