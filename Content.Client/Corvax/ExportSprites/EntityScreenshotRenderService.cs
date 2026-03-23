using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Color = Robust.Shared.Maths.Color;

namespace Content.Client.Corvax.ExportSprites;

public sealed class EntityScreenshotRenderService
{
    [Dependency] private readonly IClyde _clyde = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IEntitySystemManager _entitySystemManager = default!;
    [Dependency] private readonly IResourceManager _resourceManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IUserInterfaceManager _ui = default!;

    private EntityScreenshotRenderControl? _control;
    private bool _initialized;

    public void Initialize()
    {
        if (_initialized)
            return;

        IoCManager.InjectDependencies(this);
        _initialized = true;
    }

    public void Shutdown()
    {
        if (_control == null)
            return;

        foreach (var queued in _control.QueuedTextures)
        {
            queued.Tcs.SetCanceled();
        }

        _control.QueuedTextures.Clear();
        _ui.RootControl.RemoveChild(_control);
        _control = null;
    }

    public async Task Export(EntityUid entity,
        Direction direction,
        ResPath outputPath,
        CancellationToken cancelToken = default)
    {
        if (!_timing.IsFirstTimePredicted)
            return;

        EnsureControlAttached();

        if (!_entityManager.TryGetComponent<SpriteComponent>(entity, out var spriteComp))
            return;

        var size = GetRenderSize(spriteComp);

        if (size.Equals(Vector2i.Zero))
            return;

        var animationLayers = GetAnimatedLayers(spriteComp);
        if (animationLayers.Count == 0)
        {
            DeleteIfExists(GetAnimationDirectory(outputPath));
            await ExportFrame(entity, direction, outputPath, size, cancelToken);
            return;
        }

        var animationFrames = BuildAnimationFrames(entity, spriteComp, animationLayers);
        if (animationFrames.Count <= 1)
        {
            DeleteIfExists(GetAnimationDirectory(outputPath));
            await ExportFrame(entity, direction, outputPath, size, cancelToken);
            return;
        }

        await ExportAnimation(entity, direction, outputPath, size, spriteComp, animationLayers, animationFrames, cancelToken);
    }

    private void EnsureControlAttached()
    {
        if (!_initialized)
            Initialize();

        if (_control != null)
            return;

        _control = new EntityScreenshotRenderControl();
        _ui.RootControl.AddChild(_control);
    }

    private async Task ExportAnimation(
        EntityUid entity,
        Direction direction,
        ResPath outputPath,
        Vector2i size,
        SpriteComponent spriteComp,
        IReadOnlyList<AnimatedLayerInfo> animationLayers,
        IReadOnlyList<AnimationFrameInfo> animationFrames,
        CancellationToken cancelToken)
    {
        var originalTimes = new float[animationLayers.Count];
        for (var i = 0; i < animationLayers.Count; i++)
        {
            originalTimes[i] = spriteComp[animationLayers[i].Index].AnimationTime;
        }

        var animationDir = GetAnimationDirectory(outputPath);
        DeleteIfExists(outputPath);
        DeleteIfExists(animationDir);
        _resourceManager.UserData.CreateDir(animationDir);

        try
        {
            for (var i = 0; i < animationFrames.Count; i++)
            {
                cancelToken.ThrowIfCancellationRequested();
                ApplyAnimationTime(entity, spriteComp, animationLayers, animationFrames[i].RenderTimeSeconds);
                var framePath = animationDir / animationFrames[i].FileName;
                await ExportFrame(entity, direction, framePath, size, cancelToken);
            }

            WriteAnimationMetadata(animationDir, animationFrames);
        }
        finally
        {
            for (var i = 0; i < animationLayers.Count; i++)
            {
                _entitySystemManager.GetEntitySystem<SpriteSystem>()
                    .LayerSetAnimationTime((entity, spriteComp), animationLayers[i].Index, originalTimes[i]);
            }
        }
    }

    private async Task ExportFrame(
        EntityUid entity,
        Direction direction,
        ResPath outputPath,
        Vector2i size,
        CancellationToken cancelToken)
    {
        var texture = _clyde.CreateRenderTarget(
            new Vector2i(size.X, size.Y),
            new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8Srgb),
            name: "corvax-entity-export");

        var tcs = new TaskCompletionSource(cancelToken);
        _control!.QueuedTextures.Enqueue((texture, direction, entity, outputPath, tcs));
        await tcs.Task;
    }

    private static Vector2i GetRenderSize(SpriteComponent spriteComp)
    {
        var size = Vector2i.Zero;

        foreach (var layer in spriteComp.AllLayers)
        {
            if (!layer.Visible)
                continue;

            size = Vector2i.ComponentMax(size, layer.PixelSize);
        }

        return size;
    }

    private static ResPath GetAnimationDirectory(ResPath outputPath)
    {
        return outputPath.Directory / "_animated" / outputPath.FilenameWithoutExtension;
    }

    private void DeleteIfExists(ResPath path)
    {
        if (_resourceManager.UserData.Exists(path))
            _resourceManager.UserData.Delete(path);
    }

    private static List<AnimatedLayerInfo> GetAnimatedLayers(SpriteComponent spriteComp)
    {
        var result = new List<AnimatedLayerInfo>();
        var index = 0;

        foreach (var spriteLayer in spriteComp.AllLayers)
        {
            if (!spriteLayer.Visible ||
                !spriteLayer.AutoAnimated ||
                spriteLayer.ActualRsi == null ||
                !spriteLayer.ActualRsi.TryGetState(spriteLayer.RsiState, out var state) ||
                !state.IsAnimated ||
                state.TotalDelay <= 0f)
            {
                index++;
                continue;
            }

            result.Add(new AnimatedLayerInfo(index, state.TotalDelay, state.GetDelays()));
            index++;
        }

        return result;
    }

    private List<AnimationFrameInfo> BuildAnimationFrames(
        EntityUid entity,
        SpriteComponent spriteComp,
        IReadOnlyList<AnimatedLayerInfo> animationLayers)
    {
        const float epsilon = 0.0001f;
        const int maxFrames = 512;

        var initialSignature = BuildAnimationSignatureAt(entity, spriteComp, animationLayers, 0f, epsilon);
        var frames = new List<AnimationFrameInfo>();
        var currentFrameStart = 0f;

        for (var i = 0; i < maxFrames; i++)
        {
            var nextDelta = GetNextBoundaryDelta(currentFrameStart, animationLayers, epsilon);
            if (nextDelta <= epsilon)
                break;

            var renderTime = currentFrameStart + MathF.Min(epsilon, nextDelta * 0.5f);
            frames.Add(new AnimationFrameInfo($"{i:D4}.png", renderTime, ToDelayMilliseconds(nextDelta)));

            var nextFrameStart = currentFrameStart + nextDelta;
            if (BuildAnimationSignatureAt(entity, spriteComp, animationLayers, nextFrameStart, epsilon) == initialSignature)
                break;

            currentFrameStart = nextFrameStart;
        }

        ApplyAnimationTime(entity, spriteComp, animationLayers, 0f);
        return frames;
    }

    private void ApplyAnimationTime(
        EntityUid entity,
        SpriteComponent spriteComp,
        IReadOnlyList<AnimatedLayerInfo> animationLayers,
        float timeSeconds)
    {
        var spriteSystem = _entitySystemManager.GetEntitySystem<SpriteSystem>();

        foreach (var layer in animationLayers)
        {
            spriteSystem.LayerSetAnimationTime((entity, spriteComp), layer.Index, timeSeconds);
        }
    }

    private static string BuildAnimationSignature(
        SpriteComponent spriteComp,
        IReadOnlyList<AnimatedLayerInfo> animationLayers)
    {
        var parts = new string[animationLayers.Count];

        for (var i = 0; i < animationLayers.Count; i++)
        {
            var layer = spriteComp[animationLayers[i].Index];
            parts[i] = $"{animationLayers[i].Index}:{layer.Visible}:{layer.RsiState.Name}:{layer.AnimationFrame}";
        }

        return string.Join("|", parts);
    }

    private string BuildAnimationSignatureAt(
        EntityUid entity,
        SpriteComponent spriteComp,
        IReadOnlyList<AnimatedLayerInfo> animationLayers,
        float frameStartTime,
        float epsilon)
    {
        var nextDelta = GetNextBoundaryDelta(frameStartTime, animationLayers, epsilon);
        var probeTime = nextDelta > epsilon
            ? frameStartTime + MathF.Min(epsilon, nextDelta * 0.5f)
            : frameStartTime;

        ApplyAnimationTime(entity, spriteComp, animationLayers, probeTime);
        return BuildAnimationSignature(spriteComp, animationLayers);
    }

    private static float GetNextBoundaryDelta(
        float currentTime,
        IReadOnlyList<AnimatedLayerInfo> animationLayers,
        float epsilon)
    {
        var nextDelta = float.MaxValue;
        var foundDelta = false;

        foreach (var layer in animationLayers)
        {
            if (layer.TotalDelay <= epsilon)
                continue;

            var mod = currentTime % layer.TotalDelay;
            var cumulative = 0f;
            float? layerDelta = null;

            foreach (var delay in layer.Delays)
            {
                cumulative += delay;
                if (cumulative > mod + epsilon)
                {
                    layerDelta = cumulative - mod;
                    break;
                }
            }

            layerDelta ??= layer.TotalDelay - mod;

            if (layerDelta.Value > epsilon && layerDelta.Value < nextDelta)
            {
                nextDelta = layerDelta.Value;
                foundDelta = true;
            }
        }

        return foundDelta ? nextDelta : 0f;
    }

    private static int ToDelayMilliseconds(float seconds)
    {
        return Math.Max(1, (int) MathF.Round(seconds * 1000f));
    }

    private void WriteAnimationMetadata(ResPath animationDir, IReadOnlyList<AnimationFrameInfo> animationFrames)
    {
        using var writer = _resourceManager.UserData.OpenWriteText(animationDir / "frames.txt");
        foreach (var frame in animationFrames)
        {
            writer.WriteLine($"{frame.FileName}\t{frame.DelayMilliseconds}");
        }

        writer.Flush();
    }

    private readonly record struct AnimatedLayerInfo(int Index, float TotalDelay, float[] Delays);
    private readonly record struct AnimationFrameInfo(string FileName, float RenderTimeSeconds, int DelayMilliseconds);

    private sealed class EntityScreenshotRenderControl : Control
    {
        private static readonly Color ExportBackgroundColor = new(128, 128, 128, 0);

        [Dependency] private readonly IEntityManager _entityManager = default!;
        [Dependency] private readonly ILogManager _logManager = default!;
        [Dependency] private readonly IResourceManager _resourceManager = default!;

        internal readonly Queue<(
            IRenderTexture Texture,
            Direction Direction,
            EntityUid Entity,
            ResPath OutputPath,
            TaskCompletionSource Tcs)> QueuedTextures = new();

        private readonly ISawmill _sawmill;

        public EntityScreenshotRenderControl()
        {
            IoCManager.InjectDependencies(this);
            _sawmill = _logManager.GetSawmill("corvax.entity-sprite-export");
        }

        protected override void Draw(DrawingHandleScreen handle)
        {
            base.Draw(handle);

            while (QueuedTextures.TryDequeue(out var queued))
            {
                if (queued.Tcs.Task.IsCanceled)
                    continue;

                try
                {
                    if (!_entityManager.EntityExists(queued.Entity))
                    {
                        queued.Texture.Dispose();
                        queued.Tcs.SetResult();
                        continue;
                    }

                    var result = queued;
                    handle.RenderInRenderTarget(queued.Texture,
                        () =>
                        {
                            handle.DrawEntity(result.Entity,
                                result.Texture.Size / 2,
                                Vector2.One,
                                Angle.Zero,
                                overrideDirection: result.Direction);
                        },
                        ExportBackgroundColor);

                    if (!_resourceManager.UserData.IsDir(queued.OutputPath.Directory))
                        _resourceManager.UserData.CreateDir(queued.OutputPath.Directory);

                    var result1 = queued;
                    queued.Texture.CopyPixelsToMemory<Rgba32>(image =>
                    {
                        try
                        {
                            if (_resourceManager.UserData.Exists(result.OutputPath))
                            {
                                _sawmill.Info($"Found existing file {result.OutputPath} to replace.");
                                _resourceManager.UserData.Delete(result.OutputPath);
                            }

                            using var file = _resourceManager.UserData.OpenWrite(result.OutputPath);
                            image.SaveAsPng(file);
                            _sawmill.Info($"Saved screenshot to {result.OutputPath}");
                            result1.Tcs.SetResult();
                        }
                        catch (Exception exc)
                        {
                            if (!string.IsNullOrEmpty(exc.StackTrace))
                                _sawmill.Fatal(exc.StackTrace);

                            result1.Tcs.SetException(exc);
                        }
                        finally
                        {
                            image.Dispose();
                            result1.Texture.Dispose();
                        }
                    });
                }
                catch (Exception exc)
                {
                    queued.Texture.Dispose();

                    if (!string.IsNullOrEmpty(exc.StackTrace))
                        _sawmill.Fatal(exc.StackTrace);

                    queued.Tcs.SetException(exc);
                }
            }
        }
    }
}
