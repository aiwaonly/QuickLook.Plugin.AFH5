![QuickLook icon](https://user-images.githubusercontent.com/1687847/29485863-8cd61b7c-84e2-11e7-97d5-eacc2ba10d28.png)

# QuickLook.Plugin.AFH5

A plugin to preview Ansys Fluent cas.h5 and msh.h5 file.

<img src="./example/msh_example.png" alt="cas example" width="75%">  <img src="./example/cas_example.png" alt="msh example" width="100%">

## Structure of cas.h5

For an introduction to the H5 format, refer to this [article](https://optics.ansys.com/hc/en-us/articles/360034936913-HDF5-files).

For an introduction to the CFF format, refer to this [article](https://ansyshelp.ansys.com/public/account/secured?returnurl=/Views/Secured/corp/v252/en/flu_ug/flu_ug_app_cff.html).

<img src="./example/hdf5.png" alt="structure of cas.h5" width="40%">

After opening `.cas.h5` file using HDFView, there are following groups:

1. meshes
2. settings
3. special

The **settings** group contains multiple datasets, which contain the simulation configuration information stored as S-expression strings which you can find in the **example** folder.

1. Cortex Variables: Face IDs and surface settings
2. Domain Variables: Meaning unclear (e.g. (64 ()))
3. Origin: Ansys Fluent build information (e.g. ANSYS_FLUENT 25.2 Build 10204)
4. [Rampant Variables](https://innovationspace.ansys.com/forum/forums/topic/what-does-rp-in-rp-variable-stand-for/): Most settings
5. Solver: Solver information (e.g. ANSYS_FLUENT)
6. TGrid Variables: Geometry mesh related
7. Thread Variables: Cell zone and boundary condition
8. Version: Version information (e.g. 25.2)

## Note

> [!IMPORTANT]
> Only tested with Ansys Fluent 2025 R2.

### cas.h5

The plugin renders the case settings as indented JSON. It currently reads:

- solver
- materials
- boundary condition (cell zones and face zones)
- mesh interfaces
- custom field functions
- units
- named expressions
- solution methods and controls
- report definitions
- residuals
- monitor sets, plot sets, convergence sets
- cell registers
- iteration
- surfaces
- graphics (mesh, contours, vectors, pathlines, xy-plot, scene)
- input/output parameters

## Try out

1. Go to Release page and download the latest version.
2. Make sure that you have QuickLook running in the background. Go to your Download folder, and press `Spacebar` on the downloaded `.qlplugin` file.
3. Click the “Install” button in the popup window.
4. Restart QuickLook.
5. Select the cas.h5/msh.h5 file and press `Spacebar`.

## License

[MIT](./LICENSE)
