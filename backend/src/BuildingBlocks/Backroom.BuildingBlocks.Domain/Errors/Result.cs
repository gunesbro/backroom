using System.Diagnostics.CodeAnalysis;

namespace Backroom.BuildingBlocks.Domain.Errors;

/// <summary>
/// Outcome of an operation without a value. A struct, so returning it does not allocate.
/// Success must be stated explicitly: an uninitialized (<c>default</c>) result is a failure with
/// <see cref="Uninitialized"/>, so a forgotten code path never counts as success.
/// Expected failures use <see cref="Error"/>; exceptions are reserved for bugs and infrastructure faults.
/// </summary>
public readonly record struct Result
{
    private readonly bool _isSuccess;
    private readonly Error? _error;

    private Result(bool isSuccess, Error? error)
    {
        _isSuccess = isSuccess;
        _error = error;
    }

    /// <summary>The error of a result that was never set.</summary>
    public static Error Uninitialized { get; } =
        Error.Unexpected("result.uninitialized", "The result was not set by the code that returned it.");

    public static Result Success { get; } = new(isSuccess: true, error: null);

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => _isSuccess;

    public Error? Error => _isSuccess ? null : FailureError;

    private Error FailureError => _error ?? Uninitialized;

    public static Result Failure(Error error) => new(isSuccess: false, error ?? throw new ArgumentNullException(nameof(error)));

    public static implicit operator Result(Error error) => Failure(error);

    public override string ToString() => _isSuccess ? "Success" : $"Failure({FailureError.Code})";
}
