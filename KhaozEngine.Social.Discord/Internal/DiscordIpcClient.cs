using System;
using System.Collections.Generic;
using KhaozEngine.Social;

namespace KhaozEngine.Social.Discord.Internal;

/// <summary>
/// Speaks the Discord IPC protocol over an <see cref="IDiscordIpcTransport"/>: handshake, SET_ACTIVITY,
/// SUBSCRIBE to join events, and a non-blocking dispatch pump. Every socket operation is wrapped so a
/// failure flips the client to disconnected rather than throwing, and <see cref="Pump"/> also notices the
/// quiet death (a Discord that quit without a Close frame, which throws nothing anywhere) by asking the
/// transport whether it is still connected. Pure protocol logic, and the real socket lives in
/// <see cref="NamedPipeDiscordTransport"/>.
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
        // Re-attemptable per the ISocialProvider contract: a previous attempt that got as far as the
        // handshake can have left a partial frame in the buffer, and decoding it against the new
        // connection's bytes would desync every frame after it.
        readBuffer.Clear();
        LocalUser = null;
        IsConnected = false;

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
            return;
        }

        // A Discord that QUITS rarely says goodbye first: the socket just closes, the reader thread hits
        // end-of-stream, and nothing here reads or writes anything that would throw. So the transport going
        // !IsConnected is the only evidence of it, and without this the client stayed "connected" to a
        // client that was gone, silently dropping every SetActivity for the rest of the session (#655).
        // Read the frames Discord did manage to send BEFORE noticing, hence after the drain rather than
        // before it. The IsConnected half is what keeps a Close frame in that same drain from tearing the
        // transport down twice.
        if (IsConnected && !transport.IsConnected)
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

    // Ends the session and hands the transport back CLEAN, so the reconnect the controller is about to
    // schedule starts from nothing. Leaving the socket and its reader thread live through the whole backoff
    // was harmless while a drop ended the session for good, and is not now that one is routinely followed by
    // another connect on the same instance. Only ever called from the frame thread (every caller is a public
    // member of this class), so the transport's reader-join is never a self-join here.
    private void Disconnect()
    {
        IsConnected = false;
        LocalUser = null;
        try
        {
            transport.Disconnect();
        }
        catch (Exception)
        {
            // Suppress: this IS the failure path, and the next TryConnect tears down again anyway.
        }
    }

    public void Dispose()
    {
        Disconnect();
        transport.Dispose();
    }
}
