using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AriaUI.Tests;

public static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
            throw new AssertionException(message ?? "Expected condition to be true, but was false.");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition)
            throw new AssertionException(message ?? "Expected condition to be false, but was true.");
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException(message ?? $"Expected '{expected}', but was '{actual}'.");
    }

    public static void NotEqual<T>(T expected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException(message ?? $"Expected values not to equal '{expected}'.");
    }

    public static void Null(object? obj, string? message = null)
    {
        if (obj != null)
            throw new AssertionException(message ?? $"Expected null, but was '{obj}'.");
    }

    public static void NotNull(object? obj, string? message = null)
    {
        if (obj == null)
            throw new AssertionException(message ?? "Expected non-null value, but was null.");
    }

    public static void Contains(string expectedSubstring, string actualString)
    {
        ArgumentNullException.ThrowIfNull(actualString);
        if (!actualString.Contains(expectedSubstring, StringComparison.Ordinal))
            throw new AssertionException($"Expected string to contain '{expectedSubstring}', but was '{actualString}'.");
    }

    public static void Contains<T>(T expectedItem, IEnumerable<T> collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (!collection.Contains(expectedItem))
            throw new AssertionException($"Expected collection to contain '{expectedItem}', but it did not.");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action, string? message = null)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception other)
        {
            throw new AssertionException(message ?? $"Expected exception of type {typeof(TException).Name}, but got {other.GetType().Name}: {other.Message}");
        }

        throw new AssertionException(message ?? $"Expected exception of type {typeof(TException).Name}, but no exception was thrown.");
    }

    public static TException Throws<TException>(Action action, string? message = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception other)
        {
            throw new AssertionException(message ?? $"Expected exception of type {typeof(TException).Name}, but got {other.GetType().Name}: {other.Message}");
        }

        throw new AssertionException(message ?? $"Expected exception of type {typeof(TException).Name}, but no exception was thrown.");
    }

    public static void Fail(string? message = null)
    {
        throw new AssertionException(message ?? "Assertion explicitly failed.");
    }

    public static void Empty<T>(IEnumerable<T> collection, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.Any())
            throw new AssertionException(message ?? "Expected collection to be empty, but it contained elements.");
    }
}

public class AssertionException : Exception
{
    public AssertionException(string message) : base(message) { }
}
