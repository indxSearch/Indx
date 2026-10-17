using Microsoft.AspNetCore.Mvc;

namespace IndxServer.Services
{
    /// <summary>
    /// The API's single error-response vocabulary: every error is an
    /// RFC 9457 <see cref="ProblemDetails"/> served as
    /// <c>application/problem+json</c>, carrying a machine-readable
    /// <c>code</c> extension so clients never have to parse English prose.
    /// Status-code semantics:
    /// <list type="bullet">
    /// <item><c>404</c> — the addressed resource does not exist: dataset,
    ///   document, or team (a team you are not a member of is reported
    ///   identically to one that does not exist, so team names cannot be
    ///   enumerated).</item>
    /// <item><c>403</c> — you are a member, but your team role is too low.</item>
    /// <item><c>400</c> — the request itself is invalid (syntax, arguments,
    ///   unusable payload).</item>
    /// <item><c>409</c> — the request is valid but the dataset's current
    ///   state cannot serve it (lifecycle guard, concurrent shadow build).</item>
    /// </list>
    /// </summary>
    public static class ApiProblems
    {
        // 429 rateLimited is produced by the rate-limiter middleware (AuthRateLimiting.OnRejected),
        // not here, since it fires before any controller runs; same document shape.
        /// <summary>Builds a ProblemDetails response with the given status and machine-readable code.</summary>
        public static ObjectResult Problem(int status, string code, string title, string detail)
        {
            var problem = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Extensions = { ["code"] = code }
            };
            return new ObjectResult(problem)
            {
                StatusCode = status,
                ContentTypes = { "application/problem+json" }
            };
        }

        public static ObjectResult DatasetNotFound(string dataSetName) =>
            Problem(StatusCodes.Status404NotFound, "datasetNotFound", "Dataset not found",
                $"Dataset '{dataSetName}' does not exist in this team.");

        public static ObjectResult DocumentNotFound(long documentKey) =>
            Problem(StatusCodes.Status404NotFound, "documentNotFound", "Document not found",
                $"No document with key {documentKey} exists in the dataset.");

        /// <summary>Batch variant: names every missing key, so the caller can fix the whole batch in one round-trip.</summary>
        public static ObjectResult DocumentsNotFound(IReadOnlyCollection<long> documentKeys) =>
            Problem(StatusCodes.Status404NotFound, "documentNotFound", "Documents not found",
                $"No documents with keys [{string.Join(", ", documentKeys)}] exist in the dataset. The batch was not applied.");

        public static ObjectResult TeamNotFound(string teamName) =>
            Problem(StatusCodes.Status404NotFound, "teamNotFound", "Team not found",
                $"Team '{teamName}' does not exist, or you are not a member of it.");

        public static ObjectResult InsufficientRole(string requiredRole) =>
            Problem(StatusCodes.Status403Forbidden, "insufficientRole", "Insufficient team role",
                $"This operation requires the {requiredRole} role in the team.");

        /// <summary>
        /// The request's API key is scoped below what the operation needs (a Search key calling a
        /// write, say). Distinct from insufficientRole: the user may hold the role, the key does not.
        /// </summary>
        public static ObjectResult InsufficientKeyScope(ApiKeyLevel required) =>
            Problem(StatusCodes.Status403Forbidden, "insufficientKeyScope", "API key scope too narrow",
                required == ApiKeyLevel.Full
                    ? "This operation needs an API key with Full access. Keys limited to Search or Read cannot make it."
                    : $"This operation needs an API key with at least {ApiKeyScope.Describe(required)} access.");

        public static ObjectResult InvalidDatasetName(string dataSetName) =>
            Problem(StatusCodes.Status400BadRequest, "invalidDatasetName", "Invalid dataset name",
                $"'{dataSetName}' is not a valid dataset name.");

        public static ObjectResult InvalidArgument(string detail) =>
            Problem(StatusCodes.Status400BadRequest, "invalidArgument", "Invalid request", detail);

        /// <summary>
        /// A request that does not bind (a number given as text, a body that is not JSON): the same
        /// problem as <see cref="InvalidArgument"/>, with the field errors kept under "errors". Until
        /// Oct 2026 ASP.NET answered it with its own validation problem, the one error without a code.
        /// </summary>
        public static ObjectResult InvalidModelState(Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelState)
        {
            var problem = new ValidationProblemDetails(modelState)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request",
                Detail = "The request could not be read: " + string.Join(" ", modelState
                    .Where(entry => entry.Value is { Errors.Count: > 0 })
                    .Select(entry => $"{(string.IsNullOrEmpty(entry.Key) ? "body" : entry.Key)}: {Describe(entry.Value!.Errors[0])}")),
                Extensions = { ["code"] = "invalidArgument" }
            };
            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status400BadRequest,
                ContentTypes = { "application/problem+json" }
            };

            static string Describe(Microsoft.AspNetCore.Mvc.ModelBinding.ModelError error) =>
                !string.IsNullOrEmpty(error.ErrorMessage) ? error.ErrorMessage : error.Exception?.Message ?? "invalid value";
        }

        /// <summary>
        /// A referenced filter token could not be resolved. Distinct from invalidArgument
        /// because the recovery is specific: re-create the filter and retry with the new
        /// token, rather than fix the shape of the request.
        /// </summary>
        public static ObjectResult UnknownFilter(string detail) =>
            Problem(StatusCodes.Status400BadRequest, "unknownFilter", "Unknown filter", detail);

        /// <summary>
        /// A filter expression that makes no filter: 400 <c>invalidFilterExpression</c>, the first
        /// error as the detail, and in extensions every error (<c>kind</c>, <c>message</c>,
        /// <c>start</c>, <c>length</c>, <c>suggestion</c>; places in UTF-16 code units, as a
        /// JavaScript string counts them) and a <c>pointer</c>: the expression with a caret line
        /// under the first error.
        /// </summary>
        public static ObjectResult InvalidFilterExpression(string expression, IReadOnlyList<Indx.Api.FilterExpressionError> errors)
        {
            var first = errors.Count > 0 ? errors[0] : null;
            var result = Problem(StatusCodes.Status400BadRequest, "invalidFilterExpression", "Invalid filter expression",
                first?.Message ?? "The filter expression makes no filter.");
            var problem = (ProblemDetails)result.Value!;
            problem.Extensions["errors"] = errors.Select(e => new
            {
                kind = char.ToLowerInvariant(e.Kind.ToString()[0]) + e.Kind.ToString()[1..],
                message = e.Message,
                start = e.Start,
                length = e.Length,
                suggestion = e.Suggestion,
            }).ToArray();
            if (first != null && first.Start <= expression.Length)
                problem.Extensions["pointer"] = Pointer(expression, first.Start, first.Length);
            return result;
        }

        /// <summary>
        /// The expression with carets under [start, start + length). A long expression is cut to a
        /// window round the error, marked with "...", so a request of 100 000 characters is not sent
        /// back whole; the carets stay under the same characters.
        /// </summary>
        internal static string Pointer(string expression, int start, int length)
        {
            const int context = 60, maxMarked = 60;
            length = Math.Clamp(length, 1, maxMarked);
            int from = Math.Max(0, start - context);
            int to = Math.Min(expression.Length, start + length + context);
            string head = from > 0 ? "..." : "", tail = to < expression.Length ? "..." : "";
            return head + expression[from..to] + tail + "\n" + new string(' ', head.Length + start - from) + new string('^', length);
        }

        public static ObjectResult LoadFailed(string detail) =>
            Problem(StatusCodes.Status400BadRequest, "loadFailed", "Load failed", detail);

        public static ObjectResult OperationFailed(string detail) =>
            Problem(StatusCodes.Status400BadRequest, "operationFailed", "Operation failed", detail);

        public static ObjectResult ShadowBusy(string detail) =>
            Problem(StatusCodes.Status409Conflict, "shadowBusy", "Dataset is busy", detail);

        public static ObjectResult InvalidCredentials() =>
            Problem(StatusCodes.Status401Unauthorized, "invalidCredentials", "Authentication failed",
                "The user name or password is incorrect.");

        public static ObjectResult EmailNotConfirmed() =>
            Problem(StatusCodes.Status403Forbidden, "emailNotConfirmed", "Email not confirmed",
                "Confirm your email address before signing in. Check your inbox for the confirmation link.");
    }
}
