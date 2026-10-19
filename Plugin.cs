using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Media.Media3D;

using HelixToolkit.Wpf;
using QuickLook.Common.Plugin;

namespace QuickLook.Plugin.AFH5;

public class Plugin : IViewer
{
    public int Priority => 0;

    public void Init()
    {
    }

    public bool CanHandle(string path)
    {
        return !Directory.Exists(path) && path.ToLower().EndsWith(".cas.h5")
            || !Directory.Exists(path) && path.ToLower().EndsWith(".msh.h5");
    }

    public void Prepare(string path, ContextObject context)
    {
        if (path.ToLower().EndsWith(".cas.h5"))
        {
            context.PreferredSize = new Size { Width = 1100, Height = 700 };
            context.CanResize = true;
        }
        else
        {
            context.PreferredSize = new Size { Width = 600, Height = 600 };
        }
    }

    public void View(string path, ContextObject context)
    {
        if (path.ToLower().EndsWith(".cas.h5"))
        {
            context.ViewerContent = new CasH5Panel(path, context.Theme);
        }
        else if (path.ToLower().EndsWith("msh.h5"))
        {
            context.PreferredSize = new Size { Width = 1200, Height = 600 };

            var mesh_data = MshH5.Read(path);

            var view_point = new HelixViewport3D
            {
                ZoomExtentsWhenLoaded = true,
                ShowCoordinateSystem = true,
                IsRotationEnabled = mesh_data.Dimension == 3,
                PanGesture = new MouseGesture(MouseAction.LeftClick),
                Camera = new OrthographicCamera()
                {
                    Position = new Point3D(0, 0, 100),
                    LookDirection = new Vector3D(0, 0, -50),
                    UpDirection = new Vector3D(0, 1, 0),
                }
            };

            var render_points = new Point3DCollection();
            foreach (int index in mesh_data.Connections)
            {
                if (index >= 0 && index < mesh_data.Nodes.Count)
                {
                    render_points.Add(mesh_data.Nodes[index]);
                }
            }
            render_points.Freeze();

            var wire_frame = new LinesVisual3D
            {
                Color = Colors.Blue,
                Thickness = 0.8,
                Points = render_points,
            };
            view_point.Children.Add(wire_frame);

            context.ViewerContent = view_point;
        }

        context.Title = $"{Path.GetFileName(path)}";
        context.IsBusy = false;
    }

    public void Cleanup()
    {
    }
}
