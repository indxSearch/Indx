using System;
using System.Collections.Generic;
using System.Linq;

namespace IndxServer.Models
{
    /// <summary>
    /// Thrown when a vector or hybrid search names a field the dataset has no embedding index for —
    /// unknown, or known but never marked Embeddable. Both used to return an empty result, which a
    /// caller cannot tell from "no documents are near your vector", so the mistake looked like data.
    /// The controller maps this to <c>400 invalidArgument</c> and the message names the fields that
    /// do work, since that is the one thing the caller cannot find out from the failure itself.
    /// </summary>
    public sealed class UnknownEmbeddingFieldException(string fieldName, IEnumerable<string> embeddableFields)
        : Exception(Describe(fieldName, embeddableFields))
    {
        /// <summary>The field the request asked for.</summary>
        public string FieldName { get; } = fieldName;

        private static string Describe(string fieldName, IEnumerable<string> embeddableFields)
        {
            var available = embeddableFields.OrderBy(f => f, StringComparer.Ordinal).ToArray();
            return available.Length == 0
                ? $"Field '{fieldName}' has no embedding index: this dataset has no embeddable field. " +
                  "Mark a field Embeddable (PUT fields/embeddable), then load documents carrying its vectors."
                : $"Field '{fieldName}' has no embedding index. Embeddable fields on this dataset: " +
                  string.Join(", ", available) + ".";
        }
    }
}
