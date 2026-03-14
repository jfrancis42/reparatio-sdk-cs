using System;

namespace Reparatio
{
    /// <summary>Base exception for all Reparatio API errors.</summary>
    public class ReparatioException : Exception
    {
        public int StatusCode { get; }

        public ReparatioException(int statusCode, string message)
            : base(message) { StatusCode = statusCode; }
    }

    /// <summary>401 / 403 — missing or invalid API key.</summary>
    public class AuthenticationException : ReparatioException
    {
        public AuthenticationException(int status, string message)
            : base(status, message) { }
    }

    /// <summary>402 — higher subscription tier required.</summary>
    public class InsufficientPlanException : ReparatioException
    {
        public InsufficientPlanException(string message)
            : base(402, message) { }
    }

    /// <summary>413 — file exceeds server size limit.</summary>
    public class FileTooLargeException : ReparatioException
    {
        public FileTooLargeException(string message)
            : base(413, message) { }
    }

    /// <summary>422 — file could not be parsed.</summary>
    public class ParseException : ReparatioException
    {
        public ParseException(string message)
            : base(422, message) { }
    }
}
