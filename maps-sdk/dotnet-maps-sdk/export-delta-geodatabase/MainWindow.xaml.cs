﻿using Esri.ArcGISRuntime.ArcGISServices;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.Tasks.Offline;
using Esri.ArcGISRuntime.Tasks;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime;
using System.IO;
using System.Windows;

namespace ExportDeltaGeodatabase
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Enumeration to track which phase of the workflow the sample is in.
        private enum EditState
        {
            NotReady, // Geodatabase has not yet been generated.
            Editing, // A feature is in the process of being moved.
            Ready // The geodatabase is ready for synchronization or further edits.
        }

        // Mobile geodatabases
        private Geodatabase geodatabase;
        private Geodatabase deltaGeodatabase;

        // URI for a feature service that supports geodatabase generation.
        private Uri _featureServiceUri = new Uri("https://sampleserver6.arcgisonline.com/arcgis/rest/services/Sync/WildfireSync/FeatureServer");

        // Path to the geodatabase file on disk.
        private string _gdbPath;

        // Path to the delta geodatabase file on disk.
        private string _deltaGDBPath;

        // Task to be used for generating the geodatabase.
        private GeodatabaseSyncTask _gdbSyncTask;

        // Flag to indicate which stage of the edit process we're in.
        private EditState _readyForEdits = EditState.NotReady;

        public MainWindow()
        {
            InitializeComponent();
            _ = Initialize();
        }

        private async Task Initialize()
        {
            // Create a tile cache and load it with the SanFrancisco streets tpk.
            string tileCachePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SanFrancisco.tpkx"));
            TileCache tileCache = new TileCache(tileCachePath);

            // Create the corresponding layer based on the tile cache.
            ArcGISTiledLayer tileLayer = new ArcGISTiledLayer(tileCache);

            // Create the basemap based on the tile cache.
            Basemap sfBasemap = new Basemap(tileLayer);

            // Create the map with the tile-based basemap.
            Map myMap = new Map(sfBasemap);

            // Assign the map to the MapView.
            MyMapView.Map = myMap;

            // Create a new symbol for the extent graphic.
            SimpleLineSymbol lineSymbol = new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, System.Drawing.Color.Red, 2);

            // Create a graphics overlay for the extent graphic and apply a renderer.
            GraphicsOverlay extentOverlay = new GraphicsOverlay
            {
                Renderer = new SimpleRenderer(lineSymbol)
            };

            // Add graphics overlay to the map view.
            MyMapView.GraphicsOverlays.Add(extentOverlay);

            // Set up an event handler for when the viewpoint (extent) changes.
            MyMapView.ViewpointChanged += MapViewExtentChanged;

            try
            {
                // Create a task for generating a geodatabase (GeodatabaseSyncTask).
                _gdbSyncTask = await GeodatabaseSyncTask.CreateAsync(_featureServiceUri);

                // Add all graphics from the service to the map.
                foreach (IdInfo layer in _gdbSyncTask.ServiceInfo.LayerInfos)
                {
                    // Get the URL for this particular layer.
                    Uri onlineTableUri = new Uri(_featureServiceUri + "/" + layer.Id);

                    // Create the ServiceFeatureTable.
                    ServiceFeatureTable onlineTable = new ServiceFeatureTable(onlineTableUri);

                    // Wait for the table to load.
                    await onlineTable.LoadAsync();

                    // Skip tables that aren't for point features.{
                    if (onlineTable.GeometryType != GeometryType.Point)
                    {
                        continue;
                    }

                    // Add the layer to the map's operational layers if load succeeds.
                    if (onlineTable.LoadStatus == LoadStatus.Loaded)
                    {
                        myMap.OperationalLayers.Add(new FeatureLayer(onlineTable));
                    }
                }

                // Update the graphic - needed in case the user decides not to interact before pressing the button.
                UpdateMapExtent();

                // Enable the generate button.
                MyGenerateButton.IsEnabled = true;
            }
            catch (Exception e)
            {
                MessageBox.Show(e.ToString(), "Error");
            }
        }

        private async void GeoViewTapped(object sender, Esri.ArcGISRuntime.UI.Controls.GeoViewInputEventArgs e)
        {
            try
            {
                // Disregard if not ready for edits.
                if (_readyForEdits == EditState.NotReady) { return; }

                // If an edit is in process, finish it.
                if (_readyForEdits == EditState.Editing)
                {
                    // Hold a list of any selected features.
                    List<Feature> selectedFeatures = new List<Feature>();

                    // Get all selected features then clear selection.
                    foreach (FeatureLayer layer in MyMapView.Map.OperationalLayers)
                    {
                        // Get the selected features.
                        FeatureQueryResult layerFeatures = await layer.GetSelectedFeaturesAsync();

                        // FeatureQueryResult implements IEnumerable, so it can be treated as a collection of features.
                        selectedFeatures.AddRange(layerFeatures);

                        // Clear the selection.
                        layer.ClearSelection();
                    }

                    // Update all selected features' geometry.
                    foreach (Feature feature in selectedFeatures)
                    {
                        // Get a reference to the correct feature table for the feature.
                        GeodatabaseFeatureTable table = (GeodatabaseFeatureTable)feature.FeatureTable;

                        // Ensure the geometry type of the table is point.
                        if (table.GeometryType != GeometryType.Point)
                        {
                            continue;
                        }

                        // Set the new geometry.
                        feature.Geometry = e.Location;

                        try
                        {
                            // Update the feature in the table.
                            await table.UpdateFeatureAsync(feature);
                        }
                        catch (ArcGISException)
                        {
                            MessageBox.Show("Feature must be within extent of geodatabase.");
                        }
                    }

                    // Update the edit state.
                    _readyForEdits = EditState.Ready;

                    // Enable the sync button.
                    MySyncButton.IsEnabled = true;

                    MyDeltaButton.IsEnabled = true;

                    // Update the help label.
                    MyHelpLabel.Text = "4. Click 'Sync Geodatabase' or edit more features";
                }
                // Otherwise, start an edit.
                else
                {
                    // Define a tolerance for use with identifying the feature.
                    double tolerance = 15 * MyMapView.UnitsPerPixel;

                    // Define the selection envelope.
                    Envelope selectionEnvelope = new Envelope(e.Location.X - tolerance, e.Location.Y - tolerance, e.Location.X + tolerance, e.Location.Y + tolerance);

                    // Define query parameters for feature selection.
                    QueryParameters query = new QueryParameters()
                    {
                        Geometry = selectionEnvelope
                    };

                    // Track whether any selections were made.
                    bool selectedFeature = false;

                    // Select the feature in all applicable tables.
                    foreach (FeatureLayer layer in MyMapView.Map.OperationalLayers)
                    {
                        FeatureQueryResult res = await layer.SelectFeaturesAsync(query, SelectionMode.New);
                        selectedFeature = selectedFeature || res.Any();
                    }

                    // Only update state if a feature was selected.
                    if (selectedFeature)
                    {
                        // Set the edit state.
                        _readyForEdits = EditState.Editing;

                        // Update the help label.
                        MyHelpLabel.Text = "3. Tap on the map to move the point";
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Error");
            }
        }

        private void UpdateMapExtent()
        {
            // Return if mapview is null.
            if (MyMapView == null) { return; }

            // Get the new viewpoint.
            Viewpoint myViewPoint = MyMapView.GetCurrentViewpoint(ViewpointType.BoundingGeometry);

            // Return if viewpoint is null.
            if (myViewPoint == null) { return; }

            // Get the updated extent for the new viewpoint.
            Envelope extent = myViewPoint.TargetGeometry as Envelope;

            // Return if extent is null.
            if (extent == null) { return; }

            // Create an envelope that is a bit smaller than the extent.
            EnvelopeBuilder envelopeBldr = new EnvelopeBuilder(extent);
            envelopeBldr.Expand(0.80);

            // Get the (only) graphics overlay in the map view.
            GraphicsOverlay extentOverlay = MyMapView.GraphicsOverlays.FirstOrDefault();

            // Return if the extent overlay is null.
            if (extentOverlay == null) { return; }

            // Get the extent graphic.
            Graphic extentGraphic = extentOverlay.Graphics.FirstOrDefault();

            // Create the extent graphic and add it to the overlay if it doesn't exist.
            if (extentGraphic == null)
            {
                extentGraphic = new Graphic(envelopeBldr.ToGeometry());
                extentOverlay.Graphics.Add(extentGraphic);
            }
            else
            {
                // Otherwise, simply update the graphic's geometry.
                extentGraphic.Geometry = envelopeBldr.ToGeometry();
            }
        }

        private async Task StartGeodatabaseGeneration()
        {
            // Create a task for generating a geodatabase (GeodatabaseSyncTask).
            _gdbSyncTask = await GeodatabaseSyncTask.CreateAsync(_featureServiceUri);

            // Get the (only) graphic in the map view.
            Graphic redPreviewBox = MyMapView.GraphicsOverlays.First().Graphics.First();

            // Get the current extent of the red preview box.
            Envelope extent = redPreviewBox.Geometry as Envelope;

            // Get the default parameters for the generate geodatabase task.
            GenerateGeodatabaseParameters generateParams = await _gdbSyncTask.CreateDefaultGenerateGeodatabaseParametersAsync(extent);

            // Create a generate geodatabase job.
            GenerateGeodatabaseJob generateGdbJob = _gdbSyncTask.GenerateGeodatabase(generateParams, _gdbPath);

            // Handle the progress changed event with an inline (lambda) function to show the progress bar.
            generateGdbJob.ProgressChanged += (sender, e) =>
            {
                // Update the progress bar.
                UpdateProgressBar(generateGdbJob.Progress);
            };

            // Show the progress bar.
            MyProgressBar.Visibility = Visibility.Visible;

            // Start the job.
            generateGdbJob.Start();

            // Get the result of the job.
            geodatabase = await generateGdbJob.GetResultAsync();

            // Hide the progress bar.
            MyProgressBar.Visibility = Visibility.Collapsed;

            // Do the rest of the work.
            _ = HandleGenerationCompleted(generateGdbJob);
        }

        private async Task HandleGenerationCompleted(GenerateGeodatabaseJob job)
        {
            // If the job completed successfully, add the geodatabase to the map, replacing the layer from the service.
            if (job.Status == JobStatus.Succeeded)
            {
                // Remove the existing layers.
                MyMapView.Map.OperationalLayers.Clear();

                // Loop through all feature tables in the geodatabase and add a new layer to the map.
                foreach (GeodatabaseFeatureTable table in geodatabase.GeodatabaseFeatureTables)
                {
                    // Skip non-point tables.
                    await table.LoadAsync();
                    if (table.GeometryType != GeometryType.Point)
                    {
                        continue;
                    }

                    // Create a new feature layer for the table.
                    FeatureLayer layer = new FeatureLayer(table);

                    // Add the new layer to the map.
                    MyMapView.Map.OperationalLayers.Add(layer);
                }

                // Enable editing features.
                _readyForEdits = EditState.Ready;

                // Update help label.
                MyHelpLabel.Text = "2. Tap a point feature to select";
            }
            else
            {
                // Create a message to show the user.
                string message = "Generate geodatabase job failed";

                // Show an error message (if there is one).
                if (job.Error != null)
                {
                    message += ": " + job.Error.Message;
                }
                else
                {
                    // If no error, show messages from the job.
                    foreach (JobMessage m in job.Messages)
                    {
                        // Get the text from the JobMessage and add it to the output string.
                        message += "\n" + m.Message;
                    }
                }

                // Show the message.
                MessageBox.Show(message);
            }
        }

        private async Task SyncGeodatabase()
        {
            // Return if not ready.
            if (_readyForEdits != EditState.Ready) { return; }

            // Disable the sync button.
            MySyncButton.IsEnabled = false;

            MyDeltaButton.IsEnabled = false;

            // Create parameters for the sync task.
            SyncGeodatabaseParameters parameters = new SyncGeodatabaseParameters()
            {
                GeodatabaseSyncDirection = SyncDirection.Bidirectional,
                RollbackOnFailure = false
            };

            // Get the layer ID for each feature table in the geodatabase, then add to the sync job.
            foreach (GeodatabaseFeatureTable table in geodatabase.GeodatabaseFeatureTables)
            {
                // Get the ID for the layer.
                long id = table.ServiceLayerId;

                // Create the SyncLayerOption.
                SyncLayerOption option = new SyncLayerOption(id);

                // Add the option.
                parameters.LayerOptions.Add(option);
            }

            // Create job.
            SyncGeodatabaseJob job = _gdbSyncTask.SyncGeodatabase(parameters, geodatabase);

            // Subscribe to progress updates.
            job.ProgressChanged += (o, e) =>
            {
                // Update the progress bar.
                UpdateProgressBar(job.Progress);
            };

            // Show the progress bar.
            MyProgressBar.Visibility = Visibility.Visible;

            // Start the sync job.
            job.Start();

            // Wait for the result.
            await job.GetResultAsync();

            // Hide the progress bar.
            MyProgressBar.Visibility = Visibility.Hidden;

            // Do the remainder of the work.
            HandleSyncCompleted(job);

            deltaGeodatabase.Close();

            // Re-enable the sync button.
            MySyncButton.IsEnabled = true;

            MyDeltaButton.IsEnabled = false;
            MyReloadButton.IsEnabled = false;
        }

        private void HandleSyncCompleted(SyncGeodatabaseJob job)
        {
            // Tell the user about job completion.
            if (job.Status == JobStatus.Succeeded)
            {
                // Update the progress bar's value.
                UpdateProgressBar(0);

                MessageBox.Show("Sync task completed");
            }

            // See if the job failed.
            if (job.Status == JobStatus.Failed)
            {
                // Create a message to show the user.
                string message = "Sync geodatabase job failed";

                // Show an error message (if there is one).
                if (job.Error != null)
                {
                    message += ": " + job.Error.Message;
                }
                else
                {
                    // If no error, show messages from the job.
                    foreach (JobMessage m in job.Messages)
                    {
                        // Get the text from the JobMessage and add it to the output string.
                        message += "\n" + m.Message;
                    }
                }

                // Show the message.
                MessageBox.Show(message);
            }
        }

        private async void GenerateButton_Clicked(object sender, RoutedEventArgs e)
        {
            // Fix the selection graphic extent.
            MyMapView.ViewpointChanged -= MapViewExtentChanged;
            string path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Replicas"));

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            // Update the Geodatabase path for the new run.
            try
            {
                _gdbPath = Path.Combine(path, "temp.geodatabase");

                // Prevent duplicate clicks.
                MyGenerateButton.IsEnabled = false;

                // Call the cross-platform geodatabase generation method.
                await StartGeodatabaseGeneration();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        }

        private async void DeltaButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if(geodatabase != null && _readyForEdits == EditState.Ready)
                {
                    MyMapView.Map.OperationalLayers.Clear();
                    string path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Replicas"));
                    _deltaGDBPath = Path.Combine(path, "delta.geodatabase");
                    _ = await GeodatabaseSyncTask.ExportDeltaAsync(geodatabase, _deltaGDBPath);
                    geodatabase.Close();
                    deltaGeodatabase = await Geodatabase.OpenAsync(_deltaGDBPath);
                    foreach (GeodatabaseFeatureTable table in deltaGeodatabase.GeodatabaseFeatureTables)
                    {
                        // Skip non-point tables.
                        await table.LoadAsync();
                        if (table.GeometryType != GeometryType.Point)
                        {
                            continue;
                        }

                        // Create a new feature layer for the table.
                        FeatureLayer layer = new FeatureLayer(table);

                        // Add the new layer to the map.
                        MyMapView.Map.OperationalLayers.Add(layer);
                    }

                    // Enable editing features.
                    _readyForEdits = EditState.NotReady;
                    MySyncButton.IsEnabled = false;
                    MyDeltaButton.IsEnabled = false;
                    MyReloadButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        }

        private async void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_gdbPath != null)
                {
                    MyMapView.Map.OperationalLayers.Clear();
                    geodatabase = await Geodatabase.OpenAsync(_gdbPath);
                    deltaGeodatabase.Close();
                    foreach (GeodatabaseFeatureTable table in geodatabase.GeodatabaseFeatureTables)
                    {
                        // Skip non-point tables.
                        await table.LoadAsync();
                        if (table.GeometryType != GeometryType.Point)
                        {
                            continue;
                        }

                        // Create a new feature layer for the table.
                        FeatureLayer layer = new FeatureLayer(table);

                        // Add the new layer to the map.
                        MyMapView.Map.OperationalLayers.Add(layer);
                    }

                    // Enable editing features.
                    _readyForEdits = EditState.Ready;
                    MySyncButton.IsEnabled = true;
                    MyDeltaButton.IsEnabled = true;
                    MyReloadButton.IsEnabled = false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        }

        private void MapViewExtentChanged(object sender, EventArgs e)
        {
            // Call the cross-platform map extent update method.
            UpdateMapExtent();
        }

        private void UpdateProgressBar(int progress)
        {
            // Due to the nature of the threading implementation,
            //     the dispatcher needs to be used to interact with the UI.
            // The dispatcher takes an Action, provided here as a lambda function.
            Dispatcher.Invoke(() =>
            {
                // Update the progress bar value.
                MyProgressBar.Value = progress;
            });
        }

        private async void SyncButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await SyncGeodatabase();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        }
    }
}