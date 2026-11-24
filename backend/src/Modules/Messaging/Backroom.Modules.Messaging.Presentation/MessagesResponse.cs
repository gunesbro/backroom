namespace Backroom.Modules.Messaging.Presentation;

/// <summary><c>Next</c> is the cursor of the following page (of the kind the request used), and <c>Epoch</c> the cell epoch every number in the page belongs to.</summary>
public sealed record MessagesResponse(IReadOnlyList<MessageResponse> Items, long? Next, string Epoch);
