using Indx.Api;
using System;
using System.Collections.Generic;

namespace IndxServer.Models
{
    /// <summary>
    /// Thrown when a request's filter expression makes no filter. The controller maps it to HTTP
    /// 400 with the code <c>invalidFilterExpression</c> and the errors, each with its place in the
    /// expression. Like <see cref="UnknownFilterException"/>, it exists so that a filter that
    /// cannot be honoured is never read as "no filter", which would widen the search to everything.
    /// </summary>
    public sealed class InvalidFilterExpressionException(string expression, IReadOnlyList<FilterExpressionError> errors)
        : Exception(errors.Count > 0 ? errors[0].Message : "The filter expression makes no filter.")
    {
        public string Expression { get; } = expression;
        public IReadOnlyList<FilterExpressionError> Errors { get; } = errors;
    }
}
