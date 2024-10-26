// <copyright file="BarTextRenderer.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using BrightIdeasSoftware;

namespace CSUploader.Components
{
    public class BarTextRenderer : BarRenderer
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="BarTextRenderer"/> class.
        /// </summary>
        public BarTextRenderer()
            : base()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BarTextRenderer"/> class.
        /// </summary>
        /// <param name="minimum"></param>
        /// <param name="maximum"></param>
        public BarTextRenderer(int minimum, int maximum)
            : base(minimum, maximum)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BarTextRenderer"/> class.
        /// </summary>
        /// <param name="pen"></param>
        /// <param name="brush"></param>
        public BarTextRenderer(Pen pen, Brush brush)
            : base(pen, brush)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BarTextRenderer"/> class.
        /// </summary>
        /// <param name="pen"></param>
        /// <param name="start"></param>
        /// <param name="end"></param>
        public BarTextRenderer(Pen pen, Color start, Color end)
            : base(pen, start, end)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BarTextRenderer"/> class.
        /// </summary>
        /// <param name="minimum"></param>
        /// <param name="maximum"></param>
        /// <param name="pen"></param>
        /// <param name="brush"></param>
        public BarTextRenderer(int minimum, int maximum, Pen pen, Brush brush)
            : base(minimum, maximum, pen, brush)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BarTextRenderer"/> class.
        /// </summary>
        /// <param name="minimum"></param>
        /// <param name="maximum"></param>
        /// <param name="pen"></param>
        /// <param name="start"></param>
        /// <param name="end"></param>
        public BarTextRenderer(int minimum, int maximum, Pen pen, Color start, Color end)
            : base(minimum, maximum, pen, start, end)
        {
        }

        public override void Render(Graphics g, Rectangle r)
        {
            base.Render(g, r);

            double? progress = Aspect as double?;
            if (!progress.HasValue)
            {
                return;
            }

            string text = progress.Value == 0.0 ? "0%" : $"{progress.Value:##.##}%";

            // Create label to get width
            Label lbl = new() { AutoSize = true, Text = text, Font = Font };

            // Position label in the 'middle' of the rectangle
            float w = lbl.Width;
            float h = lbl.Height;
            float x = r.X + (r.Width / 2f) - (lbl.Width / 7);
            float y = r.Y + 3;

            lbl.Dispose();

            RectangleF rf = new(x, y, w, h);
            g.DrawString(text, Font, Brush, rf);

            MaximumWidth = r.Width;
        }
    }
}
