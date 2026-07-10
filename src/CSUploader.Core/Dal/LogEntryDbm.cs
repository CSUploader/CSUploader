// <copyright file="LogEntryDbm.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CSUploader.Dal;

[Table("LogEntry")]
public class LogEntryDbm
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public DateTime DateTime { get; set; }

    /// <summary>
    /// <see cref="Lib.LogType"/> as an int.
    /// </summary>
    public int LogType { get; set; }

    public string? Filename { get; set; }

    public string? Function { get; set; }

    public int LineNumber { get; set; }

    public int ThreadId { get; set; }

    [Required]
    public string Message { get; set; } = string.Empty;
}
