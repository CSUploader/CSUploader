// <copyright file="OperationFinishedEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib;

public class OperationFinishedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OperationFinishedEventArgs"/> class.
    /// </summary>
    /// <param name="success">if set to <c>true</c> [success].</param>
    /// <param name="startDateTime">The start date time.</param>
    public OperationFinishedEventArgs(bool success, DateTime startDateTime)
    {
        Success = success;
        TimeElapsed = DateTime.Now - startDateTime;
        DateTimeFinished = DateTime.Now;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OperationFinishedEventArgs"/> class.
    /// </summary>
    protected OperationFinishedEventArgs()
    {
    }

    /// <summary>
    /// Gets or sets a value indicating whether the operation was successful.
    /// </summary>
    public virtual bool Success { get; protected set; }

    /// <summary>
    /// Gets or sets the time elapsed of the operation.
    /// </summary>
    public virtual TimeSpan TimeElapsed { get; protected set; }

    /// <summary>
    /// Gets or sets the date time the operation finished.
    /// </summary>
    public virtual DateTime DateTimeFinished { get; protected set; }
}
