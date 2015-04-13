﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using GuiSupport;
using MathSupport;
using Raster;
using Utilities;

namespace _029flow
{
  public partial class Form1 : Form
  {
    /// <summary>
    /// Current output raster image. Locked access.
    /// </summary>
    protected Bitmap outputImage = null;

    /// <summary>
    /// The same order as items in the comboScenes.
    /// </summary>
    protected List<InitWorldDelegate> worldInitFunctions = null;

    /// <summary>
    /// Index of the current (selected) scene.
    /// </summary>
    protected volatile int selectedWorld = 0;

    /// <summary>
    /// Image width in pixels, 0 for default value (according to panel size).
    /// </summary>
    protected int ImageWidth = 0;

    /// <summary>
    /// Image height in pixels, 0 for default value (according to panel size).
    /// </summary>
    protected int ImageHeight = 0;

    /// <summary>
    /// Current working array of simulators (one for each working thread).
    /// </summary>
    protected List<FluidSimulator> sims = null;

    /// <summary>
    /// Global stopwatch for rendering thread. Locked access.
    /// </summary>
    protected Stopwatch sw = new Stopwatch();

    /// <summary>
    /// Master thread - rendering, data collection.
    /// </summary>
    protected Thread aThread = null;

    class SyncObject
    {
      public Bitmap bmp;

      public long totalSpawned;

      public double simTime;
    }

    protected class SimulationProgress : Progress
    {
      protected Form1 f;

      public bool pressure = false;

      protected long lastSync = 0L;

      public SimulationProgress ( Form1 _f )
      {
        f = _f;
      }

      public void Reset ()
      {
        lastSync = 0L;
      }

      public bool NeedsSync ()
      {
        if ( f.sw.ElapsedMilliseconds - lastSync < SyncInterval )
          return false;

        return true;
      }
      
      public override void Sync ( Object msg )
      {
        if ( !NeedsSync() )
          return;

        lastSync = f.sw.ElapsedMilliseconds;
        SyncObject so = msg as SyncObject;
        if ( so == null )
          return;

        f.SetText( String.Format( CultureInfo.InvariantCulture, "Sync {0:f1}s: sim {1:f1}s, spawned {2}",
                   1.0e-3 * lastSync, so.simTime, Util.kmg( so.totalSpawned ) ) );
        Bitmap nb;
        lock ( so.bmp )
          nb = (Bitmap)so.bmp.Clone();
        f.SetImage( nb );
      }
    }

    /// <summary>
    /// Progress info / user break handling.
    /// </summary>
    protected SimulationProgress progress = null;

    /// <summary>
    /// Worker-thread-specific data.
    /// </summary>
    protected class WorkerThreadInit
    {
      /// <summary>
      /// Fluid simulator instance.
      /// </summary>
      public FluidSimulator sim;

      public int nPart;

      public float ppt;

      public double dt;

      public double vart;

      public WorkerThreadInit ( FluidSimulator s, int n, float pptake, double deltat, double variancet )
      {
        sim = s;
        nPart = n;
        ppt = pptake;
        dt = deltat;
        vart = variancet;
      }
    }

    /// <summary>
    /// Routine of one worker-thread.
    /// Collect arrays and rendering progress are the only two shared objects.
    /// </summary>
    /// <param name="spec">Thread-specific data (worker-thread-selector).</param>
    private void SimulationWorker ( Object spec )
    {
      WorkerThreadInit init = spec as WorkerThreadInit;
      if ( init != null )
      {
        init.sim.Init( init.nPart, init.ppt );

        // infinite simulation loop:
        do
        {
          double deltaT = init.dt + init.vart * (init.sim.rnd.UniformNumber() - 1.0);
          init.sim.Tick( deltaT );
          init.sim.SimTime += deltaT;
          lock ( progress )
            if ( !progress.Continue ) break;
          init.sim.GatherBuffers();
        }
        while ( true );
      }
    }

    FluidSimulator publicSim = null;

    /// <summary>
    /// Total simulation time in seconds.
    /// </summary>
    double SimTime = 0.0;

    /// <summary>
    /// Total Spawned particles in all workers.
    /// </summary>
    long TotalSpawned = 0L;

    /// <summary>
    /// Buffer dimensions.
    /// </summary>
    int width = 100, height = 100;

    /// <summary>
    /// Buffer for particle density.
    /// </summary>
    int[ , ] cell = null;

    /// <summary>
    /// Buffers for velocity components / sum of total square velocity.
    /// </summary>
    float[ , ] vx, vy, power;

    /// <summary>
    /// Mouse probe.
    /// </summary>
    private void probe ( int x, int y )
    {
      int cellLoc = 0;
      double vxLoc = 0.0, vyLoc = 0.0, powerLoc = 0.0;

      if ( cell == null ) return;
      lock ( cell )
      {
        if ( y < 0 || y >= cell.GetLength( 0 ) ||
             x < 0 || x >= cell.GetLength( 1 ) )
          return;

        cellLoc  = cell[ y, x ];
        vxLoc    = vx[ y, x ];
        vyLoc    = vy[ y, x ];
        powerLoc = power[ y, x ];
      }

      // local state of the field: cellLoc, vxLoc, vyLoc, powerLoc
      double RMSpower = 0.0, Mvx = 0.0, Mvy = 0.0;
      if ( cellLoc > 0 )
      {
        RMSpower = Math.Sqrt( powerLoc / cellLoc );
        Mvx = vxLoc / cellLoc;
        Mvy = vyLoc / cellLoc;
      }

      // show results:
      labelSample.Text = String.Format( CultureInfo.InvariantCulture, "State[{0},{1}]: particles={2}({3}), Mv=[{4:f4},{5:f4}], RMSpower={6:f4}",
                                        x, y, cellLoc, Util.kmg( TotalSpawned ), Mvx, Mvy, RMSpower );
    }

    /// <summary>
    /// Runs the simulation (in separate thread[s]).
    /// </summary>
    private void RunSimulation ()
    {
      Cursor.Current = Cursors.WaitCursor;
      cell = null;

      // determine output image size:
      width = ImageWidth;
      if ( width <= 0 ) width = panel1.Width;
      height = ImageHeight;
      if ( height <= 0 ) height = panel1.Height;

      // allocate & init simulator array:
      int threads = Math.Max( 1, Environment.ProcessorCount - 1 );
      if ( !checkMultithreading.Checked ) threads = 1;
      sims = new List<FluidSimulator>( threads );
      int t;
      for ( t = 0; t < threads; t++ )
        sims.Add( getSimulator( t ) );
      foreach ( var sim in sims )
      {
        sim.SetPresentationSize( ref width, ref height );
        sim.InitBuffers();
      }
      publicSim = sims[ 0 ];

      // output presentation image:
      if ( outputImage != null )
        outputImage.Dispose();
      outputImage = new Bitmap( width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb );
      SyncObject so = new SyncObject();
      so.bmp = outputImage;

      TotalSpawned = 0L;
      SimTime = 0.0;
      cell  = new int[ height, width ];
      vx    = new float[ height, width ];
      vy    = new float[ height, width ];
      power = new float[ height, width ];

      // progress & timer:
      progress.SyncInterval = ((width * (long)height) > (2L << 20)) ? 30000L : 10000L;
      progress.Reset();
      lock ( sw )
      {
        sw.Reset();
        sw.Start();
      }

      // run the simulators:
      Thread[] pool = new Thread[ threads ];
      for ( t = 0; t < threads; t++ )
        pool[ t ] = new Thread( new ParameterizedThreadStart( this.SimulationWorker ) );
      for ( t = threads; --t >= 0; )
        pool[ t ].Start( new WorkerThreadInit( sims[ t ], 8000, 4500.0f, 0.004, 0.004 ) );

      do
      {
        Thread.Sleep( 2000 );
        bool pressure = false;
        lock ( progress )
        {
          if ( !progress.Continue ) break;
          if ( !progress.NeedsSync() ) continue;
          pressure = progress.pressure;
        }

        // 1. collect data from all workers:
        int x, y;
        lock ( cell )
        {
          lock ( sims[ 0 ].cell )
          {
            TotalSpawned = sims[ 0 ].GetTotalSpawned();
            SimTime = sims[ 0 ].SimTime;
            System.Array.Copy( sims[ 0 ].cell,   cell, width * height );
            System.Array.Copy( sims[ 0 ].vx,       vx, width * height );
            System.Array.Copy( sims[ 0 ].vy,       vy, width * height );
            System.Array.Copy( sims[ 0 ].power, power, width * height );
          }
          for ( t = 1; t < threads; t++ )
            lock ( sims[ t ].cell )
            {
              TotalSpawned += sims[ t ].GetTotalSpawned();
              SimTime += sims[ t ].SimTime;
              for ( y = 0; y < height; y++ )
                for ( x = 0; x < width; x++ )
                {
                  cell[ y, x ]  += sims[ t ].cell[ y, x ];
                  vx[ y, x ]    += sims[ t ].vx[ y, x ];
                  vy[ y, x ]    += sims[ t ].vy[ y, x ];
                  power[ y, x ] += sims[ t ].power[ y, x ];
                }
            }
        }

        // 2. default visualization of the pressure / velocity
        int r, g, b, num;
        Color col;

        double maxV2N = 0.001;
        double V2N;
        double maxV = 0.0;
        int n;
        for ( y = 2; y < height - 2; y++ )    // avoid borders..
          for ( x = 2; x < width - 2; x++ )
            if ( (n = cell[ y, x ]) > 0 )
            {
              if ( (V2N = n * power[ y, x ]) > maxV2N )
                maxV2N = V2N;
              double mvx = Math.Abs( vx[ y, x ] / n );
              double mvy = Math.Abs( vy[ y, x ] / n );
              if ( mvx > maxV )
                maxV = mvx;
              if ( mvy > maxV )
                maxV = mvy;
            }

        double pressMul = 2.0 / Math.Sqrt( maxV2N );
        double vMul = 128.0 / maxV;

        for ( y = 0; y < height; y++ )
          for ( x = 0; x < width; x++ )
          {
            if ( (num = cell[ y, x ]) < 1 )
              col = Color.FromArgb( 0, 0, 128 );
            else
              if ( pressure )
                col = Draw.ColorRamp( pressMul * Math.Sqrt( num * power[ y, x ] ) );
              else
              {
                r = (int)(128 + vMul * vx[ y, x ] / num);
                g = (int)(128 + vMul * vy[ y, x ] / num);
                b = 0;
                col = Color.FromArgb( Arith.Clamp( r, 0, 255 ),
                                      Arith.Clamp( g, 0, 255 ),
                                      Arith.Clamp( b, 0, 255 ) );
              }
            so.bmp.SetPixel( x, y, col );
          }
        so.simTime = SimTime;
        so.totalSpawned = TotalSpawned;

        progress.Sync( so );
      }
      while ( true );

      // wait for the simulator threads:
      for ( t = 0; t < threads; t++ )
      {
        pool[ t ].Join();
        pool[ t ] = null;
      }

      long elapsed;
      lock ( sw )
      {
        sw.Stop();
        elapsed = sw.ElapsedMilliseconds;
      }

      String msg = String.Format( CultureInfo.InvariantCulture,
                                  "{0:f1}s  [ {1}x{2}, mt{3}, sim{4:f1}s, spawned{5} ]",
                                  1.0e-3 * elapsed, width, height, threads,
                                  SimTime, Util.kmg( TotalSpawned ) );
      SetText( msg );
      Console.WriteLine( "Simulation finished: " + msg );
      SetImage( (Bitmap)outputImage.Clone() );

      Cursor.Current = Cursors.Default;

      StopSimulation();
    }

    delegate void SetImageCallback ( Bitmap newImage );

    protected void SetImage ( Bitmap newImage )
    {
      if ( pictureBox1.InvokeRequired )
      {
        SetImageCallback si = new SetImageCallback( SetImage );
        BeginInvoke( si, new object[] { newImage } );
      }
      else
      {
        pictureBox1.Image = newImage;
        pictureBox1.Invalidate();
      }
    }

    delegate void SetTextCallback ( string text );

    protected void SetText ( string text )
    {
      if ( labelElapsed.InvokeRequired )
      {
        SetTextCallback st = new SetTextCallback( SetText );
        BeginInvoke( st, new object[] { text } );
      }
      else
        labelElapsed.Text = text;
    }

    delegate void StopSimulationCallback ();

    protected void StopSimulation ()
    {
      if ( aThread == null )
        return;

      if ( buttonSimulation.InvokeRequired )
      {
        StopSimulationCallback ea = new StopSimulationCallback( StopSimulation );
        BeginInvoke( ea );
      }
      else
      {
        // actually stop the rendering:
        lock ( progress )
        {
          progress.Continue = false;
        }
        aThread.Join();
        aThread = null;

        // GUI stuff:
        SimModeGUI( false );
      }
    }

    public Form1 ()
    {
      InitializeComponent();
      progress = new SimulationProgress( this );
      String []tok = "$Rev$".Split( ' ' );
      Text += " (rev: " + tok[1] + ')';

      // Init scenes etc.
      InitializeScenes();
      buttonRes.Text = FormResolution.GetLabel( ref ImageWidth, ref ImageHeight );
    }

    protected void SimModeGUI ( bool sim )
    {
      buttonSimulation.Enabled =
      comboScene.Enabled    =
      buttonRes.Enabled     =
      buttonSave.Enabled    = !sim;
      buttonStop.Enabled    = sim;
    }

    private void buttonRes_Click ( object sender, EventArgs e )
    {
      FormResolution form = new FormResolution( ImageWidth, ImageHeight );
      if ( form.ShowDialog() == DialogResult.OK )
      {
        ImageWidth = form.ImageWidth;
        ImageHeight = form.ImageHeight;
        buttonRes.Text = FormResolution.GetLabel( ref ImageWidth, ref ImageHeight );
      }
    }

    private void buttonSimulation_Click ( object sender, EventArgs e )
    {
      if ( aThread != null )
        return;

      SimModeGUI( true );
      lock ( progress )
      {
        progress.Continue = true;
      }

      aThread = new Thread( new ThreadStart( this.RunSimulation ) );
      aThread.Start();
    }

    private void buttonStop_Click ( object sender, EventArgs e )
    {
      StopSimulation();
    }

    private void buttonSave_Click ( object sender, EventArgs e )
    {
      if ( outputImage == null ||
           aThread != null ) return;

      SaveFileDialog sfd = new SaveFileDialog();
      sfd.Title = "Save PNG file";
      sfd.Filter = "PNG Files|*.png";
      sfd.AddExtension = true;
      sfd.FileName = "";
      if ( sfd.ShowDialog() != DialogResult.OK )
        return;

      outputImage.Save( sfd.FileName, System.Drawing.Imaging.ImageFormat.Png );
    }

    private void buttonResults_Click ( object sender, EventArgs e )
    {
      if ( cell == null ) return;

      SaveFileDialog sfd = new SaveFileDialog();
      sfd.Title = "Save CSV file";
      sfd.Filter = "CSV Files|*.csv";
      sfd.AddExtension = true;
      sfd.FileName = "";
      if ( sfd.ShowDialog() != DialogResult.OK )
        return;

      if ( cell != null )
        lock ( cell )
          using ( StreamWriter wri = new StreamWriter( sfd.FileName ) )
          {
            wri.WriteLine( "\"world\";\"sim-time\";\"spawned\";\"width\";\"height\"" );
            wri.WriteLine( string.Format( CultureInfo.InvariantCulture, "\"{0}\";{1:f1};{2};{3};{4}",
                                          comboScene.Items[ selectedWorld ].ToString(), SimTime, TotalSpawned, width, height ) );
            wri.WriteLine( "\"x\";\"y\";\"particles\";\"mean vx\";\"mean vy\";\"rms v\"" );
            double scale = 1.0 / publicSim.scalexy;
            for ( int y = 0; y < height; y++ )
              for ( int x = 0; x < width; x++ )
              {
                int n = cell[ y, x ];
                int denom = Math.Max( n, 1 );
                wri.WriteLine( string.Format( CultureInfo.InvariantCulture, "{0:f5};{1:f5};{2};{3:f5};{4:f5};{5:f5}",
                                              x * scale, y * scale, n, vx[ y, x ] / denom, vy[ y, x ] / denom,
                                              Math.Sqrt( power[ y, x ] / denom ) ) );
              }
          }
    }

    private void comboScene_SelectedIndexChanged ( object sender, EventArgs e )
    {
      StopSimulation();
      selectedWorld = comboScene.SelectedIndex;
    }

    private void checkPressure_CheckedChanged ( object sender, EventArgs e )
    {
      lock ( progress )
        progress.pressure = checkPressure.Checked;
    }

    private void pictureBox1_MouseDown ( object sender, MouseEventArgs e )
    {
      if ( cell != null && e.Button == MouseButtons.Left )
        probe( e.X, e.Y );
    }

    private void pictureBox1_MouseMove ( object sender, MouseEventArgs e )
    {
      if ( cell != null && e.Button == MouseButtons.Left )
        probe( e.X, e.Y );
    }

    private void Form1_FormClosing ( object sender, FormClosingEventArgs e )
    {
      StopSimulation();
    }
  }
}