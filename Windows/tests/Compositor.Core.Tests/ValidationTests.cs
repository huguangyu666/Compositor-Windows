using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The gates the macOS ProjectStore applies before a manifest is accepted, exercised one at a
/// time: version gates, appearance gates, hierarchy rules, live-mask chains, guides and limits.
/// </summary>
public class ValidationTests
{
    private static ProjectManifest ValidManifest(int version = ProjectManifest.CurrentVersion)
    {
        var id = Guid.NewGuid();
        return new ProjectManifest
        {
            Version = version,
            DocumentId = Guid.NewGuid(),
            Width = 64,
            Height = 64,
            ActiveLayerId = id,
            Layers =
            [
                new ProjectLayerRecord
                {
                    Id = id,
                    Name = "Layer",
                    IsVisible = true,
                    ImageFile = ProjectValidator.Uuid(id) + ".png",
                    Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(64, 64) },
                },
            ],
        };
    }

    [Fact]
    public void AcceptsTheBaseline()
    {
        ProjectValidator.Validate(ValidManifest());
        ProjectValidator.Validate(ValidManifest(version: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(-1)]
    public void RejectsUnsupportedVersions(int version)
    {
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectValidator.Validate(ValidManifest(version)));
        Assert.Equal(ProjectErrorKind.Version, ex.Kind);
        Assert.Equal(version, ex.Version);
    }

    [Fact]
    public void RejectsWrongFormatAndColorSpace()
    {
        var wrongFormat = ValidManifest();
        wrongFormat.Format = "com.something.else";
        AssertKind(ProjectErrorKind.Invalid, wrongFormat);

        var wrongSpace = ValidManifest();
        wrongSpace.ColorSpace = "Display P3";
        AssertKind(ProjectErrorKind.Invalid, wrongSpace);
    }

    [Fact]
    public void RejectsOutOfRangeCanvases()
    {
        var zero = ValidManifest();
        zero.Width = 0;
        AssertKind(ProjectErrorKind.TooLarge, zero);

        var huge = ValidManifest();
        huge.Height = Limits.MaxSide + 1;
        AssertKind(ProjectErrorKind.TooLarge, huge);
    }

    [Fact]
    public void RejectsTooManyLayers()
    {
        var manifest = ValidManifest();
        var template = manifest.Layers[0];
        while (manifest.Layers.Count <= Limits.MaxLayers)
        {
            var copy = new ProjectLayerRecord
            {
                Id = Guid.NewGuid(),
                Name = "L",
                IsVisible = true,
                Transform = template.Transform,
            };
            manifest.Layers.Add(copy);
        }
        AssertKind(ProjectErrorKind.TooLarge, manifest);
    }

    [Fact]
    public void ImageFilesMustBeNamedAfterTheirLayer()
    {
        var manifest = ValidManifest();
        manifest.Layers[0].ImageFile = "background.png";
        AssertKind(ProjectErrorKind.Invalid, manifest);

        manifest.Layers[0].ImageFile = ProjectValidator.Uuid(manifest.Layers[0].Id).ToLowerInvariant() + ".png";
        AssertKind(ProjectErrorKind.Invalid, manifest);
    }

    [Fact]
    public void MasksRequireTheirVersionAndName()
    {
        var manifest = ValidManifest(version: 3);
        manifest.Layers[0].MaskFile = ProjectValidator.Uuid(manifest.Layers[0].Id) + ".mask.png";
        AssertKind(ProjectErrorKind.Invalid, manifest);

        var modern = ValidManifest();
        modern.Layers[0].MaskFile = ProjectValidator.Uuid(modern.Layers[0].Id) + ".mask.png";
        modern.Layers[0].MaskEnabled = true;
        ProjectValidator.Validate(modern);

        modern.Layers[0].MaskFile = "mask.png";
        AssertKind(ProjectErrorKind.Invalid, modern);
    }

    [Fact]
    public void GroupMasksRequireVersion6()
    {
        var folderId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var manifest = ValidManifest(version: 5);
        manifest.Layers[0].Id = childId;
        manifest.Layers[0].ImageFile = ProjectValidator.Uuid(childId) + ".png";
        manifest.ActiveLayerId = childId;
        manifest.Layers.Insert(0, new ProjectLayerRecord
        {
            Id = folderId,
            Name = "Folder",
            IsVisible = true,
            IsGroup = true,
            MaskFile = ProjectValidator.Uuid(folderId) + ".mask.png",
            Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(64, 64) },
        });
        manifest.Layers[1].ParentId = folderId;
        AssertKind(ProjectErrorKind.Invalid, manifest);

        manifest.Version = 6;
        ProjectValidator.Validate(manifest);
    }

    [Fact]
    public void FoldersTakeOpacityOnlyFromVersion8()
    {
        var folderId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var manifest = ValidManifest(version: 7);
        manifest.Layers[0].Id = childId;
        manifest.Layers[0].ImageFile = ProjectValidator.Uuid(childId) + ".png";
        manifest.ActiveLayerId = childId;
        manifest.Layers.Insert(0, new ProjectLayerRecord
        {
            Id = folderId,
            Name = "Folder",
            IsVisible = true,
            IsGroup = true,
            Opacity = 0.5,
            Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(64, 64) },
        });
        manifest.Layers[1].ParentId = folderId;
        AssertKind(ProjectErrorKind.Invalid, manifest);

        manifest.Version = 8;
        ProjectValidator.Validate(manifest);
    }

    [Fact]
    public void Version1RequiresDefaultAppearance()
    {
        var manifest = ValidManifest(version: 1);
        manifest.Layers[0].Opacity = 0.5;
        AssertKind(ProjectErrorKind.Invalid, manifest);

        manifest.Layers[0].Opacity = 1;
        manifest.Layers[0].BlendMode = LayerBlendMode.Multiply;
        AssertKind(ProjectErrorKind.Invalid, manifest);
    }

    [Fact]
    public void GroupsMustNotCarryImages()
    {
        var manifest = ValidManifest();
        manifest.Layers[0].IsGroup = true;
        AssertKind(ProjectErrorKind.Invalid, manifest);
    }

    [Fact]
    public void ParentChainsMustBeAcyclicGroups()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var manifest = ValidManifest();
        manifest.Layers.Add(new ProjectLayerRecord
        {
            Id = first,
            Name = "A",
            IsVisible = true,
            IsGroup = true,
            ParentId = second,
            Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(64, 64) },
        });
        manifest.Layers.Add(new ProjectLayerRecord
        {
            Id = second,
            Name = "B",
            IsVisible = true,
            IsGroup = true,
            ParentId = first,
            Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(64, 64) },
        });
        AssertKind(ProjectErrorKind.Invalid, manifest);
    }

    [Fact]
    public void ParentsMustBeGroups()
    {
        var parentId = Guid.NewGuid();
        var manifest = ValidManifest();
        manifest.Layers[0].ParentId = parentId;
        manifest.Layers.Add(new ProjectLayerRecord
        {
            Id = parentId,
            Name = "Not a group",
            IsVisible = true,
            Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(64, 64) },
        });
        AssertKind(ProjectErrorKind.Invalid, manifest);
    }

    [Fact]
    public void Version1RejectsGroups()
    {
        var manifest = ValidManifest(version: 1);
        manifest.Layers[0].IsGroup = false;
        manifest.Layers[0].ParentId = null;
        manifest.Layers.Add(new ProjectLayerRecord
        {
            Id = Guid.NewGuid(),
            Name = "Folder",
            IsVisible = true,
            IsGroup = true,
            Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(64, 64) },
        });
        AssertKind(ProjectErrorKind.Invalid, manifest);
    }

    [Fact]
    public void AdjustmentLayersRequireVersion7AndNoImage()
    {
        var manifest = ValidManifest(version: 6);
        manifest.Layers[0].ImageFile = null;
        manifest.Layers[0].Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Invert };
        AssertKind(ProjectErrorKind.Invalid, manifest);

        manifest.Version = 7;
        ProjectValidator.Validate(manifest);

        manifest.Layers[0].ImageFile = ProjectValidator.Uuid(manifest.Layers[0].Id) + ".png";
        AssertKind(ProjectErrorKind.Invalid, manifest);
    }

    [Fact]
    public void SampleBlurKindsRequireVersion9()
    {
        var manifest = ValidManifest(version: 7);
        manifest.Layers[0].ImageFile = null;
        manifest.Layers[0].Adjustment = new LayerAdjustment { Kind = AdjustmentKind.GaussianBlur, BlurRadius = 12.5 };
        AssertKind(ProjectErrorKind.Invalid, manifest);

        manifest.Version = 9;
        ProjectValidator.Validate(manifest);
    }

    [Fact]
    public void OutOfRangeAdjustmentValuesAreRejected()
    {
        var manifest = ValidManifest();
        manifest.Layers[0].ImageFile = null;
        manifest.Layers[0].Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Exposure, ExposureSettings = new ExposureSettings { Exposure = 21 } };
        AssertKind(ProjectErrorKind.Invalid, manifest);

        var adjustment = new LayerAdjustment
        {
            Kind = AdjustmentKind.Curves,
            Curves = new CurvesSettings
            {
                Channels =
                [
                    new() { new CurvePoint(0, 0), new CurvePoint(120, 147), new CurvePoint(255, 255) },
                    new() { new CurvePoint(0, 0), new CurvePoint(255, 255) },
                    new() { new CurvePoint(0, 0), new CurvePoint(255, 255) },
                    new() { new CurvePoint(0, 0), new CurvePoint(255, 255) },
                ],
            },
        };
        manifest.Layers[0].Adjustment = adjustment;
        ProjectValidator.Validate(manifest);

        adjustment.Curves.Channels[1][1] = new CurvePoint(100, 260);
        AssertKind(ProjectErrorKind.Invalid, manifest);
    }

    [Fact]
    public void LiveMasksMustChainThroughPlainLayers()
    {
        var source = Guid.NewGuid();
        var manifest = ValidManifest(version: 4);
        manifest.Layers[0].MaskSourceId = source;
        AssertKind(ProjectErrorKind.Invalid, manifest); // version gate

        var valid = ValidManifest(version: 5);
        valid.Layers[0].MaskSourceId = source;
        valid.Layers.Add(new ProjectLayerRecord
        {
            Id = source,
            Name = "Source",
            IsVisible = false,
            Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(64, 64) },
        });
        ProjectValidator.Validate(valid);

        valid.Layers[1].IsGroup = true;
        valid.Layers[1].ImageFile = null;
        AssertKind(ProjectErrorKind.Invalid, valid); // group source

        var missing = ValidManifest(version: 5);
        missing.Layers[0].MaskSourceId = Guid.NewGuid();
        AssertKind(ProjectErrorKind.Invalid, missing);

        var self = ValidManifest(version: 5);
        self.Layers[0].MaskSourceId = self.Layers[0].Id;
        AssertKind(ProjectErrorKind.Invalid, self);
    }

    [Fact]
    public void GuidesAreGatedOnVersion8()
    {
        var manifest = ValidManifest(version: 7);
        manifest.Guides = [];
        ProjectValidator.Validate(manifest);

        manifest.Guides.Add(new CanvasGuide { Id = Guid.NewGuid(), Axis = GuideAxis.Horizontal, Position = 32 });
        AssertKind(ProjectErrorKind.Invalid, manifest);

        manifest.Version = 8;
        ProjectValidator.Validate(manifest);
    }

    [Fact]
    public void ActiveLayerMustExist()
    {
        var manifest = ValidManifest();
        manifest.ActiveLayerId = Guid.NewGuid();
        AssertKind(ProjectErrorKind.Invalid, manifest);
    }

    [Fact]
    public void LayerNamesMustNotBeBlank()
    {
        var manifest = ValidManifest();
        manifest.Layers[0].Name = "   ";
        AssertKind(ProjectErrorKind.Invalid, manifest);
    }

    [Fact]
    public void TransformsAreBounded()
    {
        var manifest = ValidManifest();
        manifest.Layers[0].Transform.Size = new SizeD(0, 64);
        AssertKind(ProjectErrorKind.Invalid, manifest);

        manifest.Layers[0].Transform.Size = new SizeD(64, 300_001);
        AssertKind(ProjectErrorKind.Invalid, manifest);

        manifest.Layers[0].Transform.Size = new SizeD(64, 300_000);
        ProjectValidator.Validate(manifest);

        manifest.Layers[0].Transform.Size = new SizeD(64, 64);
        manifest.Layers[0].Transform.Origin = new PointD(1_000_001, 0);
        AssertKind(ProjectErrorKind.Invalid, manifest);

        manifest.Layers[0].Transform.Origin = new PointD(double.NaN, 0);
        AssertKind(ProjectErrorKind.Invalid, manifest);
    }

    [Fact]
    public void ResolutionIsBounded()
    {
        var manifest = ValidManifest();
        manifest.Resolution = 0.5;
        AssertKind(ProjectErrorKind.Invalid, manifest);

        manifest.Resolution = 9600;
        ProjectValidator.Validate(manifest);
    }

    private static void AssertKind(ProjectErrorKind kind, ProjectManifest manifest) =>
        Assert.Equal(kind, Assert.Throws<ProjectFormatException>(() => ProjectValidator.Validate(manifest)).Kind);
}
