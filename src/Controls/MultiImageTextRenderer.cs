// <copyright file="MultiImageTextRenderer.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using BrightIdeasSoftware;
using System.Collections;

namespace CSUploader.Controls;

public class MultiImageTextRenderer : BaseRenderer
{
    public MultiImageTextRenderer()
    {
    }

    public override void Render(Graphics g, Rectangle r)
    {
        DrawBackground(g, r);

        object imageSelector = OLVSubItem.ImageSelector;

        int offset = 0;
        if (imageSelector is ICollection collection)
        {
            // We want the first icons to line up
            // DrawImages() calculates the .X with 1 pixel padding
            // Remove it here so the .X lines with all other first images
            r.X -= 1;
            offset = DrawImages(g, r, collection);
        }
        else if (imageSelector is Image image)
        {
            offset = DrawImage(g, r, image);
        }

        if (Aspect is string text && !string.IsNullOrEmpty(text))
        {
            r.X += offset;
            r.Width -= offset;

            DrawText(g, r, text);
        }
    }
}
