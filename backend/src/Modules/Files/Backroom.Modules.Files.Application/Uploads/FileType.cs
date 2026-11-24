using Backroom.Modules.Files.Contracts;

namespace Backroom.Modules.Files.Application.Uploads;

/// <summary>An allowed content type: its kind and the signature its content must carry.</summary>
internal sealed record FileType(string ContentType, FileKind Kind, ContentFamily Family);
