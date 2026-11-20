using System.Diagnostics.CodeAnalysis;

namespace Backroom.BuildingBlocks.Domain.Errors;

/// <summary>
/// Outcome of an operation that produces a value on success. An uninitialized (<c>default</c>)
/// result is a failure with <see cref="Result.Uninitialized"/>.
/// </summary>
public readonly record struct Result<T>
{
    private readonly bool _isSuccess;
    private readonly T? _value;
    private readonly Error? _error;

    private Result(T value)
    {
        _isSuccess = true;
        _value = value;
        _error = null;
    }

    private Result(Error error)
    {
        _isSuccess = false;
        _value = default;
        _error = error;
    }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => _isSuccess;

    public Error? Error => _isSuccess ? null : FailureError;

    private Error FailureError => _error ?? Result.Uninitialized;

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);

    /// <summary>The value; throws when the result is a failure, so check <see cref="IsSuccess"/> first.</summary>
    public T GetValueOrThrow() =>
        _isSuccess ? _value! : throw new InvalidOperationException($"Result is a failure: {FailureError.Code}.");

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return _isSuccess ? onSuccess(_value!) : onFailure(FailureError);
    }

    public override string ToString() => _isSuccess ? $"Success({_value})" : $"Failure({FailureError.Code})";
}
