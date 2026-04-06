// <copyright file="ContentDisposition.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Specialized;
using System.Text.RegularExpressions;

namespace CSUploader.Lib.Net.Http;

public class ContentDisposition
{
    private static readonly Regex ContentDispositionRegex = new("^([^;]+);(?:\\s*([^=]+)=((?<q>\"?)[^\"]*\\k<q>);?)*$", RegexOptions.Compiled);

    public ContentDisposition(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            throw new ArgumentNullException("s");
        }

        Match match = ContentDispositionRegex.Match(s);
        if (!match.Success)
        {
            throw new FormatException("input is not a valid content-disposition string.");
        }

        var typeGroup = match.Groups[1];
        var nameGroup = match.Groups[2];
        var valueGroup = match.Groups[3];

        int groupCount = match.Groups.Count;
        int paramCount = nameGroup.Captures.Count;

        Type = typeGroup.Value;
        Parameters = [];

        for (int i = 0; i < paramCount; i++)
        {
            string name = nameGroup.Captures[i].Value;
            string value = valueGroup.Captures[i].Value;

            if (name.Equals("filename", StringComparison.InvariantCultureIgnoreCase))
            {
                FileName = value;
            }
            else
            {
                Parameters.Add(name, value);
            }
        }
    }

    public string? FileName { get; }

    public StringDictionary Parameters { get; } = [];

    public string? Type { get; }
}
