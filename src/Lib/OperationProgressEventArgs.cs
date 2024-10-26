// <copyright file="OperationProgressEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib
{
    /// <summary>
    /// Progress of an operation.
    /// </summary>
    public class OperationProgressEventArgs : EventArgs
    {
        private long size = 0;

        private long bytesProcessed = 0;

        private DateTime dateTimeStarted;

        /// <summary>Initializes a new instance of the <see cref="OperationProgressEventArgs" /> class.</summary>
        /// <param name="size">The size.</param>
        /// <param name="bytesProcessed">The bytes processed.</param>
        /// <param name="dateTimeStarted">The date time started.</param>
        public OperationProgressEventArgs(long size, long bytesProcessed, DateTime dateTimeStarted)
        {
            this.size = size;
            this.bytesProcessed = bytesProcessed;
            this.dateTimeStarted = dateTimeStarted;

            Calculate();
        }

        /// <summary>Initializes a new instance of the <see cref="OperationProgressEventArgs" /> class.</summary>
        protected OperationProgressEventArgs()
        {
        }

        /// <summary>
        /// Gets or sets the size of the operation.
        /// </summary>
        public virtual long Size
        {
            get
            {
                return size;
            }

            protected set
            {
                size = value;

                Calculate();
            }
        }

        /// <summary>
        /// Gets the Operation speed, in bytes.
        /// </summary>
        public virtual long Speed { get; private set; }

        /// <summary>
        /// Gets the progress of the operation, in percentage.
        /// </summary>
        public virtual double Progress { get; private set; }

        /// <summary>
        /// Gets or sets the amount of bytes processed.
        /// </summary>
        public virtual long BytesProcessed
        {
            get
            {
                return bytesProcessed;
            }

            protected set
            {
                bytesProcessed = value;

                Calculate();
            }
        }

        /// <summary>
        /// Gets the amount of bytes remaining to be processed.
        /// </summary>
        public virtual long BytesRemaining { get; private set; }

        /// <summary>
        /// Gets or sets the start date time of the operation.
        /// </summary>
        public virtual DateTime DateTimeStarted
        {
            get
            {
                return dateTimeStarted;
            }

            protected set
            {
                dateTimeStarted = value;

                Calculate();
            }
        }

        /// <summary>
        /// Gets the elapsed time since the operation has started.
        /// </summary>
        public virtual TimeSpan TimeElapsed { get; private set; }

        /// <summary>
        /// Gets the remaining time until operation is done.
        /// </summary>
        public virtual TimeSpan TimeRemaining { get; private set; }

        /// <summary>
        /// Gets the estimated finish date time of the operation.
        /// </summary>
        public virtual DateTime DateTimeFinish { get; private set; }

        /// <summary>
        /// Calculates the value of the remaining properties.
        /// </summary>
        protected void Calculate()
        {
            if (BytesProcessed > 0)
            {
                BytesRemaining = Size - BytesProcessed;
                Progress = (100.0 / Size) * BytesProcessed;
                TimeElapsed = DateTime.Now - DateTimeStarted;
                Speed = TimeElapsed.Ticks > 0 ? (long)(BytesProcessed / TimeElapsed.TotalSeconds) : 0;
                DateTimeFinish = DateTime.Now.Add(TimeRemaining);
                TimeRemaining = TimeSpan.FromSeconds(BytesProcessed > 0 ? TimeElapsed.TotalSeconds / BytesProcessed * BytesRemaining : 0);
            }
        }
    }
}
