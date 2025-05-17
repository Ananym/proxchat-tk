using System;

namespace ProxChatClient.Services;

public class SignalingConnectionException : Exception
{
    public SignalingConnectionException(string message) : base(message)
    {
    }

    public SignalingConnectionException(string message, Exception innerException) 
        : base(message, innerException)
    {
    }
} 