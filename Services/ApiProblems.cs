using Microsoft.AspNetCore.Mvc;

namespace IndxCloudApi.Services
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

        public static ObjectResult InvalidDatasetName(string dataSetName) =>
            Problem(StatusCodes.Status400BadRequest, "invalidDatasetName", "Invalid dataset name",
                $"'{dataSetName}' is not a valid dataset name.");

        public static ObjectResult InvalidArgument(string detail) =>
            Problem(StatusCodes.Status400BadRequest, "invalidArgument", "Invalid request", detail);

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
