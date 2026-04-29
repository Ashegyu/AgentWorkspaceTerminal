using System;

namespace AgentWorkspace.Client.Wire;

/// <summary>
/// Thrown by the client when the daemon answers an RPC with an error envelope.
/// </summary>
public sealed class RpcException : Exception
{
    public RpcException(int code, string message) : base($"RPC error {code}: {message}")
    {
        Code = code;
        ServerMessage = message;
    }

    public int Code { get; }
    public string ServerMessage { get; }
}
