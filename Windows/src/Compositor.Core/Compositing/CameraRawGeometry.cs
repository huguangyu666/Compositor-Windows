namespace Compositor.Core;

/// <summary>
/// The numeric half of Camera Raw's Geometry band: the four corners the picture is bent onto, and the crop that
/// can trim what is left of it. It mirrors <c>CameraRawGeometryCalibration.swift:25-172</c> — the normalization,
/// the <c>adjusts</c> guard, <c>outputCorners</c> and the Constrain Crop branch built on
/// <c>brush_alpha_bounds</c> (BrushPixels.c:3) — and it runs ahead of every other pass, because on the Mac the
/// geometry step replaces the source image before a single color is touched (<c>CameraRaw.swift:234-236</c>).
///
/// The corners and the crop arithmetic are the Mac's own, so they are locked number for number
/// (Windows/tools/geometry-golden.py). The pixels are a different story: the Mac hands its corners to Core
/// Image's <c>CIPerspectiveTransform</c>, which this machine has neither the framework nor the OS to run, so the
/// warp goes through the port's own inverse-homography sampler (DistortWarp). Where the two cannot disagree —
/// an offset that lands on whole pixels — the port is held to the Mac's bytes; everywhere else the pixel claims
/// are directional, and the corner numbers carry the exactness instead.
/// </summary>
public static class CameraRawGeometry
{
    /// <summary>Every slider in its own range and every line long enough to read a direction off, the Mac's
    /// <c>normalized</c> (CameraRawGeometryCalibration.swift:51-64). A value that is not a number falls back to
    /// that slider's own zero, as the Mac's clamp does; a line under a hundredth of the frame is dropped, which is
    /// what keeps "Guided, nothing drawn" from bending anything.</summary>
    public static void Normalize(CameraRawSettings settings)
    {
        settings.GeometryVertical = CameraRawSettings.SliderValue(settings.GeometryVertical, CameraRawSettings.ToneMin, CameraRawSettings.ToneMax, 0);
        settings.GeometryHorizontal = CameraRawSettings.SliderValue(settings.GeometryHorizontal, CameraRawSettings.ToneMin, CameraRawSettings.ToneMax, 0);
        settings.GeometryRotate = CameraRawSettings.SliderValue(settings.GeometryRotate, CameraRawSettings.GeometryRotateMin, CameraRawSettings.GeometryRotateMax, 0);
        settings.GeometryAspect = CameraRawSettings.SliderValue(settings.GeometryAspect, CameraRawSettings.ToneMin, CameraRawSettings.ToneMax, 0);
        settings.GeometryScale = CameraRawSettings.SliderValue(settings.GeometryScale, CameraRawSettings.ToneMin, CameraRawSettings.ToneMax, 0);
        settings.GeometryOffsetX = CameraRawSettings.SliderValue(settings.GeometryOffsetX, CameraRawSettings.ToneMin, CameraRawSettings.ToneMax, 0);
        settings.GeometryOffsetY = CameraRawSettings.SliderValue(settings.GeometryOffsetY, CameraRawSettings.ToneMin, CameraRawSettings.ToneMax, 0);
        settings.GeometryGuides = settings.GeometryGuides.Where(guide => guide.Length > 0.01).ToList();
    }

    /// <summary>What the drawn lines ask for: the first line's angle, folded into ±45°, is the rotation; a second
    /// line that runs more vertical than horizontal contributes 25 of keystone, signed by which way it leans, and
    /// one that runs more horizontal than vertical contributes 25 of the other axis. The amounts are fixed —
    /// the lines set direction only, never strength (<c>guidedCorrections</c>, :112-132).</summary>
    public static (double Vertical, double Horizontal, double Rotate) GuidedCorrections(
        IReadOnlyList<CameraRawGeometryGuide> guides)
    {
        if (guides.Count == 0) return (0, 0, 0);
        var first = guides[0];
        double dx = first.EndX - first.StartX, dy = first.EndY - first.StartY;
        if (Math.Sqrt(dx * dx + dy * dy) <= 1e-4) return (0, 0, 0);
        double rotate = -Math.Atan2(dy, dx) * 180 / Math.PI;
        if (rotate > 45) rotate -= 90; else if (rotate < -45) rotate += 90;
        double vertical = 0, horizontal = 0;
        if (guides.Count > 1)
        {
            var second = guides[1];
            double sx = second.EndX - second.StartX, sy = second.EndY - second.StartY;
            if (Math.Sqrt(sx * sx + sy * sy) > 1e-4)
            {
                double angle = Math.Atan2(sy, sx) * 180 / Math.PI;
                vertical = Math.Abs(angle) > 45 ? (angle > 0 ? 25 : -25) : 0;
                horizontal = Math.Abs(angle) <= 45 ? (angle > 0 ? 25 : -25) : 0;
            }
        }
        return (vertical, horizontal, rotate);
    }

    /// <summary>Guided adds to the sliders rather than replacing them; Off leaves the drawn lines unread
    /// (<c>effectiveCorrections</c>, :102-110).</summary>
    public static (double Vertical, double Horizontal, double Rotate) EffectiveCorrections(CameraRawSettings settings)
    {
        if (settings.GeometryUpright != CameraRawSettings.GeometryUprightGuided)
            return (settings.GeometryVertical, settings.GeometryHorizontal, settings.GeometryRotate);
        var guided = GuidedCorrections(settings.GeometryGuides);
        return (settings.GeometryVertical + guided.Vertical, settings.GeometryHorizontal + guided.Horizontal,
            settings.GeometryRotate + guided.Rotate);
    }

    /// <summary>The four corners in the surface's own top-down coordinates, handle order TL, TR, BR, BL —
    /// <c>outputCorners</c> (CameraRawGeometryCalibration.swift:135-172) with its y axis turned over at the end.
    /// The keystone strength rides on the projection: Rectilinear reaches a little past half as far.</summary>
    public static PointD[] Corners(CameraRawSettings settings, int width, int height)
    {
        var (verticalOf, horizontalOf, rotateOf) = EffectiveCorrections(settings);
        double w = width, h = height;
        double strength = CameraRawSettings.GeometryProjectionStrength(settings.GeometryProjection);
        double vertical = verticalOf / 100 * w * 0.18 * strength;
        double horizontal = horizontalOf / 100 * h * 0.18 * strength;
        double aspectScale = 1 + settings.GeometryAspect / 200;
        double zoom = 1 + settings.GeometryScale / 100;
        double shiftX = settings.GeometryOffsetX / 100 * w * 0.15;
        double shiftY = settings.GeometryOffsetY / 100 * h * 0.15;
        var topLeft = new PointD(-vertical + shiftX, h + shiftY);
        var topRight = new PointD(w + vertical + shiftX, h + shiftY);
        var bottomRight = new PointD(w + horizontal + shiftX, -shiftY);
        var bottomLeft = new PointD(-horizontal + shiftX, -shiftY);
        var center = new PointD(w / 2 + shiftX, h / 2 + shiftY);
        double radians = rotateOf * Math.PI / 180;
        double cosine = Math.Cos(radians), sine = Math.Sin(radians);
        PointD Rotated(PointD point)
        {
            double dx = point.X - center.X, dy = point.Y - center.Y;
            return new PointD(center.X + dx * cosine - dy * sine, center.Y + dx * sine + dy * cosine);
        }

        (topLeft, topRight, bottomRight, bottomLeft) = (Rotated(topLeft), Rotated(topRight), Rotated(bottomRight), Rotated(bottomLeft));
        // Aspect is deliberately not a scale: width stretches and height pinches by the same factor, so the
        // picture keeps its area instead of its shape.
        if (aspectScale != 1)
        {
            PointD Scaled(PointD point) => new(center.X + (point.X - center.X) * aspectScale,
                center.Y + (point.Y - center.Y) / aspectScale);
            (topLeft, topRight, bottomRight, bottomLeft) = (Scaled(topLeft), Scaled(topRight), Scaled(bottomRight), Scaled(bottomLeft));
        }

        if (zoom != 1)
        {
            PointD Zoomed(PointD point) => new(center.X + (point.X - center.X) * zoom, center.Y + (point.Y - center.Y) * zoom);
            (topLeft, topRight, bottomRight, bottomLeft) = (Zoomed(topLeft), Zoomed(topRight), Zoomed(bottomRight), Zoomed(bottomLeft));
        }

        // The Mac's y runs upward from the bottom of the pixel grid; this surface's runs downward from the top.
        PointD Down(PointD point) => new(point.X, h - point.Y);
        return [Down(topLeft), Down(topRight), Down(bottomRight), Down(bottomLeft)];
    }

    /// <summary>The warped layer, always the size it went in at. Returns the source itself when there is nothing
    /// to bend, or when the corners have collapsed into a quad no sampler can invert — Scale at −100 does exactly
    /// that, pinching every corner onto the center. The Mac leaves that case to Core Image, which has no
    /// documented answer; refusing here is the port's own decision, and the panel says so out loud.</summary>
    public static Surface Apply(Surface source, CameraRawSettings settings)
    {
        var grade = settings.Clone();
        Normalize(grade);
        if (!grade.AdjustsGeometry) return source;
        var corners = Corners(grade, source.Width, source.Height);
        if (!DistortWarp.IsUsable(corners)) return source;
        var placement = new LayerTransform
        {
            Origin = PointD.Zero, Size = new SizeD(source.Width, source.Height), Sampling = LayerSampling.Smooth,
        };
        var (warped, bound) = DistortWarp.Warp(source, placement, corners);
        var frame = IntoFrame(warped, (int)bound.Origin.X, (int)bound.Origin.Y, source.Width, source.Height);
        return grade.GeometryConstrainCrop ? ConstrainCrop(frame) : frame;
    }

    /// <summary>Copies the whole-pixel bounds the warp returned back into the layer's own frame, dropping whatever
    /// falls outside it — which is what keeping the output the size of the input means here.</summary>
    private static Surface IntoFrame(Surface warped, int originX, int originY, int width, int height)
    {
        var frame = new Surface(width, height);
        for (int y = 0; y < warped.Height; y++)
        {
            int targetY = originY + y;
            if (targetY < 0 || targetY >= height) continue;
            for (int x = 0; x < warped.Width; x++)
            {
                int targetX = originX + x;
                if (targetX < 0 || targetX >= width) continue;
                int source = y * warped.Stride + x * 4, target = targetY * frame.Stride + targetX * 4;
                frame.Pixels[target] = warped.Pixels[source];
                frame.Pixels[target + 1] = warped.Pixels[source + 1];
                frame.Pixels[target + 2] = warped.Pixels[source + 2];
                frame.Pixels[target + 3] = warped.Pixels[source + 3];
            }
        }
        return frame;
    }

    /// <summary>The opaque bounds of a surface, in the order <c>brush_alpha_bounds</c> writes them: the first and
    /// last column that carry any coverage anywhere, and the first row and the one past the last. Rows are scanned
    /// in memory order, so this is top-down like the surface itself. A fully transparent picture reports a zero
    /// rect rather than an inverted one (BrushPixels.c:3-21).</summary>
    public static (int Left, int Top, int Right, int Bottom) BoundsOf(Surface image)
    {
        int left = image.Width, right = 0, top = image.Height, bottom = 0;
        for (int y = 0; y < image.Height; y++)
        {
            int row = y * image.Stride;
            int first = 0;
            while (first < image.Width && image.Pixels[row + first * 4 + 3] == 0) first++;
            if (first == image.Width) continue;
            int last = image.Width;
            while (last > first && image.Pixels[row + (last - 1) * 4 + 3] == 0) last--;
            if (first < left) left = first;
            if (last > right) right = last;
            if (y < top) top = y;
            bottom = y + 1;
        }
        return (right == 0 ? 0 : left, right == 0 ? 0 : top, right, bottom);
    }

    /// <summary>What Constrain Crop does with those bounds: the rect it trims to, the fit that returns it to the
    /// frame's own size, and where that lands. Null when there is nothing to trim — either nothing at all, or
    /// coverage that already reaches every edge (CameraRawGeometryCalibration.swift:90-97).</summary>
    public static (RectI Crop, double Scale, RectD Draw)? CropFit((int Left, int Top, int Right, int Bottom) bounds,
        int width, int height)
    {
        var crop = new RectI(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        if (!(crop.Width >= 1 && crop.Height >= 1 && (crop.Width < width || crop.Height < height))) return null;
        double scale = Math.Min((double)width / crop.Width, (double)height / crop.Height);
        double drawWidth = crop.Width * scale, drawHeight = crop.Height * scale;
        return (crop, scale, new RectD((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight));
    }

    /// <summary>Trims the empty edges and fits what is left back into the frame, centered. The resample is the
    /// port's own; the rect arithmetic is the Mac's.</summary>
    public static Surface ConstrainCrop(Surface frame)
    {
        if (CropFit(BoundsOf(frame), frame.Width, frame.Height) is not { } fit) return frame;
        var cropped = new Surface(fit.Crop.Width, fit.Crop.Height);
        for (int y = 0; y < fit.Crop.Height; y++)
            Array.Copy(frame.Pixels, (y + fit.Crop.Y) * frame.Stride + fit.Crop.X * 4,
                cropped.Pixels, y * cropped.Stride, fit.Crop.Width * 4);
        var result = new Surface(frame.Width, frame.Height);
        Resampler.Draw(cropped, new LayerTransform
        {
            Origin = new PointD(fit.Draw.X, fit.Draw.Y), Size = new SizeD(fit.Draw.Width, fit.Draw.Height),
            Sampling = LayerSampling.Smooth,
        }, result, 0, 0, frame.Width, frame.Height);
        return result;
    }
}

/// <summary>One drawn guide: a line in the layer's own normalized space, 0…1 with y measured down from the
/// top-left — which is what the Mac's picker actually writes (<c>CameraRaw.swift:601-607</c> maps the click
/// through <c>previewMapping.inverted()</c> into top-down pixel rows), not what its struct comment claims
/// ("from the lower-left", <c>CameraRawGeometryCalibration.swift:14-23</c>). The port follows the code. Only the
/// first two lines are ever read, and only for direction — a line sets which way to straighten, never how far
/// (<c>CameraRawGeometryCalibration.swift:112-132</c>).</summary>
public readonly record struct CameraRawGeometryGuide(double StartX, double StartY, double EndX, double EndY)
{
    public double Length => Math.Sqrt((EndX - StartX) * (EndX - StartX) + (EndY - StartY) * (EndY - StartY));
}
