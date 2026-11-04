using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Assimp;
using HelixToolkit.SharpDX.Model.Scene;
using HelixToolkit.WinUI.SharpDX;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Numerics;

namespace UI.Views;

public sealed partial class ModelViewerDialog : ContentDialog
{
    private readonly HelixToolkitScene _scene;
    private readonly DefaultEffectsManager _effectsManager = new();
    private Vector3 _center;
    private float _extent = 1;

    public ModelViewerDialog(HelixToolkitScene scene, string path)
    {
        InitializeComponent();
        _scene = scene;
        Title = $"3D preview — {Path.GetFileName(path)}";
        var sceneGroup = new SceneNodeGroupModel3D();
        sceneGroup.AddNode(scene.Root);
        ScenePresenter.Content = sceneGroup;
        Viewport.EffectsManager = _effectsManager;
        Closed += (_, _) => _effectsManager.Dispose();
        ResetCamera();
        PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            ResetCamera();
        };
    }

    private void ResetCamera()
    {
        if (_scene.Root.TryGetBound(out var bounds))
        {
            _center = bounds.Center;
            _extent = Math.Max(Math.Max(bounds.Width, bounds.Height), bounds.Depth);
            if (!float.IsFinite(_extent) || _extent < 0.001f)
                _extent = 1;
        }

        var distance = _extent * 2.7f;
        var position = _center + new Vector3(_extent * 0.75f, _extent * 0.65f, distance);
        Viewport.FixedRotationPoint = _center;
        Viewport.Camera = new PerspectiveCamera
        {
            Position = position,
            LookDirection = _center - position,
            UpDirection = Vector3.UnitY,
            NearPlaneDistance = Math.Max(_extent / 1000, 0.001f),
            FarPlaneDistance = _extent * 1000,
            FieldOfView = 45
        };
    }
}
