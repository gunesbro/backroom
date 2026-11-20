namespace Backroom.Modules.Email.IntegrationTests;

/// <summary>How <see cref="FakeSmtpServer"/> breaks a conversation.</summary>
internal enum FakeSmtpFault
{
    None,

    /// <summary>Accepts the message (250 after the final dot), then closes the connection without waiting for QUIT.</summary>
    CloseAfterAccepting,

    /// <summary>Reads the whole message, then closes the connection before replying to the final dot.</summary>
    CloseBeforeDataReply,

    /// <summary>Starts the reply to the final dot ("250 2.0") and closes the connection in the middle of it.</summary>
    CloseDuringDataReply,

    /// <summary>Never answers EHLO, so the client waits before anything is handed over.</summary>
    StallOnEhlo,
}
