using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.IO;
using System.Data;
using System.Data.SQLite;
using System.Threading;

namespace MapCombine
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        CancellationTokenSource cancellationTokenSource;
        public MainWindow()
        {
            InitializeComponent();
        }

        private async void btnBegin_Click(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(fpSource.SelectedFile) || !File.Exists(fpDestination.SelectedFile))
            {
                txtInfoDisp.Text = "请先选择源文件和目的文件！";
                return;
            }

            Button btn = sender as Button;
            txtInfoDisp.Text = string.Empty;
            var progress = new Progress<double>(i => { progressBar.Value = i; lblPercent.Content = i.ToString("f2") + "%"; });
            cancellationTokenSource = new CancellationTokenSource();    // 每次使用时都需要重新 new，CancellationTokenSource一旦取消后是不能复位的。
            btnCancel.IsEnabled = true;
            btnCombineImages.IsEnabled = false;
            btnCombineMap.IsEnabled = false;
            btnCalcMap.IsEnabled = false;
            bool completed = false;
            switch (btn.Name)
            {
                case "btnCombineImages":
                    completed = await CombineImagesAsync(fpSource.SelectedFile, fpDestination.SelectedFile, progress, cancellationTokenSource);
                    break;
                case "btnCombineMap":
                    completed = await CombineMapAsync(fpSource.SelectedFile, fpDestination.SelectedFile, progress, cancellationTokenSource);
                    break;
                case "btnCalcMap":
                    completed = await CalcWriteMapAsync(fpSource.SelectedFile, fpDestination.SelectedFile, progress, cancellationTokenSource);
                    break;
            }

            if (completed)
            {
                txtInfoDisp.Text = "操作已完成！";
            }
            else
            {
                txtInfoDisp.Text = "数据格式不兼容，或操作已被用户取消！";
            }
            btnCancel.IsEnabled = false;
            btnCombineImages.IsEnabled = true;
            btnCombineMap.IsEnabled = true;
            btnCalcMap.IsEnabled = true;
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            cancellationTokenSource.Cancel();
        }

        Task<bool> CombineImagesAsync(string sourcefile, string destfile, IProgress<double> progress, CancellationTokenSource cancelTokenSource)
        {
            return Task.Run(() => CombineImages(sourcefile, destfile, progress, cancelTokenSource));
        }

        bool CombineImages(string sourcefile, string destfile, IProgress<double> progress, CancellationTokenSource cancelTokenSource)
        {
            bool bRtn = true;
            SQLiteConnection sqlSource = new SQLiteConnection("Data Source=" + sourcefile + ";Version=3;");
            sqlSource.Open();
            SQLiteCommand cmdSource = new SQLiteCommand(sqlSource);
            SQLiteTransaction transSource = sqlSource.BeginTransaction();   // 开启一个新事务，加快操作速度。
            cmdSource.Transaction = transSource;
            cmdSource.CommandText = "SELECT COUNT(*) FROM images";
            SQLiteDataReader readerSource = cmdSource.ExecuteReader();
            int recTotal = 0;
            if (readerSource.Read())
            {
                this.Dispatcher.Invoke(new Action(() => lblTotalRec.Content = recTotal = readerSource.GetInt32(0)));
            }
            readerSource.Close();

            cmdSource.CommandText = "SELECT * FROM images";
            readerSource = cmdSource.ExecuteReader();

            SQLiteConnection sqlDest = new SQLiteConnection("Data Source=" + destfile + ";Version=3;");
            sqlDest.Open();
            SQLiteCommand cmdDest = new SQLiteCommand(sqlDest);
            SQLiteTransaction transDest = sqlDest.BeginTransaction();
            cmdDest.Transaction = transDest;
            int recCnt = 1;
            while (readerSource.Read())
            {
                string tile_id = (string)readerSource["tile_id"];
                byte[] tile_data = (byte[])readerSource["tile_data"];

                cmdDest.CommandText = "SELECT tile_id FROM images WHERE tile_id = '" + tile_id + "'";
                SQLiteDataReader readerDest = cmdDest.ExecuteReader();
                if (readerDest.Read())
                {
                    readerDest.Close();
                }
                else
                {
                    readerDest.Close();         // 执行新SQL命令前，先关闭前执行的命令。
                    cmdDest.CommandText = "INSERT INTO images VALUES(@tile_data, @tile_id)";
                    SQLiteParameter[] sqlParams =
                    {
                        new SQLiteParameter("@tile_data", DbType.Object),
                        new SQLiteParameter("@tile_id", DbType.String)
                    };
                    sqlParams[0].Value = tile_data;
                    sqlParams[1].Value = tile_id;
                    cmdDest.Parameters.AddRange(sqlParams);
                    cmdDest.ExecuteNonQuery();
                    readerDest.Close();         // 关闭SQL命令，为下次执行做准备。
                }
                this.Dispatcher.Invoke(new Action(() => lblCurrentRec.Content = recCnt++));
                progress.Report((double)recCnt * 100.0 / (double)recTotal);
                if (cancelTokenSource.IsCancellationRequested)
                {
                    bRtn = false;
                    break;
                }
            }
            readerSource.Close();

            transDest.Commit();
            sqlDest.Close();

            transSource.Commit();               // 提交当前事务
            sqlSource.Close();
            return bRtn;
        }

        Task<bool> CombineMapAsync(string sourcefile, string destfile, IProgress<double> progress, CancellationTokenSource cancelTokenSource)
        {
            return Task.Run(() => CombineMap(sourcefile, destfile, progress, cancelTokenSource));
        }

        bool CombineMap(string sourcefile, string destfile, IProgress<double> progress, CancellationTokenSource cancelTokenSource)
        {
            bool bRtn = true;
            SQLiteConnection sqlSource = new SQLiteConnection("Data Source=" + sourcefile + ";Version=3;");
            sqlSource.Open();
            using (SQLiteCommand cmdSource = new SQLiteCommand(sqlSource))
            {
                // 开启一个新事务，加快操作速度。如果操作还是慢，需要为表 map 的 tile_id 列创建主键。
                SQLiteTransaction transSource = sqlSource.BeginTransaction();
                cmdSource.Transaction = transSource;

                int recTotal = 0;
                cmdSource.CommandText = "SELECT COUNT(*) FROM map";
                using (SQLiteDataReader readerSource = cmdSource.ExecuteReader())
                {
                    if (readerSource.Read())
                    {
                        this.Dispatcher.Invoke(new Action(() => lblTotalRec.Content = recTotal = readerSource.GetInt32(0)));
                    }
                }

                cmdSource.CommandText = "SELECT * FROM map";
                using (SQLiteDataReader readerSource = cmdSource.ExecuteReader())
                {
                    SQLiteConnection sqlDest = new SQLiteConnection("Data Source=" + destfile + ";Version=3;");
                    sqlDest.Open();
                    using (SQLiteCommand cmdDest = new SQLiteCommand(sqlDest))
                    {
                        SQLiteTransaction transDest = sqlDest.BeginTransaction();
                        cmdDest.Transaction = transDest;

                        int recCnt = 1;
                        while (readerSource.Read())
                        {
                            string tile_id = (string)readerSource["tile_id"];
                            cmdDest.CommandText = "SELECT tile_id FROM map WHERE tile_id = '" + tile_id + "'";
                            using (SQLiteDataReader readerDest = cmdDest.ExecuteReader())
                            {
                                if (!readerDest.Read())
                                {
                                    long zoom_level = (long)readerSource["zoom_level"];
                                    long tile_column = (long)readerSource["tile_column"];
                                    long tile_row = (long)readerSource["tile_row"];

                                    readerDest.Close();
                                    cmdDest.CommandText = "INSERT INTO map VALUES(@zoom_level, @tile_column, @tile_row, @tile_id)";
                                    SQLiteParameter[] sqlParams =
                                    {
                                        new SQLiteParameter("@zoom_level", DbType.Int64),
                                        new SQLiteParameter("@tile_column", DbType.Int64),
                                        new SQLiteParameter("@tile_row", DbType.Int64),
                                        new SQLiteParameter("@tile_id", DbType.String)
                                    };
                                    sqlParams[0].Value = zoom_level;
                                    sqlParams[1].Value = tile_column;
                                    sqlParams[2].Value = tile_row;
                                    sqlParams[3].Value = tile_id;
                                    cmdDest.Parameters.AddRange(sqlParams);
                                    cmdDest.ExecuteNonQuery();
                                }
                                this.Dispatcher.Invoke(new Action(() => lblCurrentRec.Content = recCnt++));
                                progress.Report((double)recCnt * 100.0 / (double)recTotal);
                                if (cancelTokenSource.IsCancellationRequested)
                                {
                                    bRtn = false;
                                    break;
                                }
                            }
                        }
                        transDest.Commit();
                        sqlDest.Close();
                    }
                }
                transSource.Commit();
            }
            sqlSource.Close();
            return bRtn;
        }

        Task<bool> CalcWriteMapAsync(string sourcefile, string destfile, IProgress<double> progress, CancellationTokenSource cancelTokenSource)
        {
            return Task.Run(() => CalcWriteMap(sourcefile, destfile, progress, cancelTokenSource));
        }

        bool CalcWriteMap(string sourcefile, string destfile, IProgress<double> progress, CancellationTokenSource cancelTokenSource)
        {
            bool bRtn = true;
            SQLiteConnection sqlDest = new SQLiteConnection("Data Source=" + destfile + ";Version=3;");
            sqlDest.Open();
            SQLiteCommand cmdDest = new SQLiteCommand("SELECT tile_id FROM images", sqlDest);
            SQLiteTransaction transDest = sqlDest.BeginTransaction();
            cmdDest.Transaction = transDest;
            SQLiteDataReader readerDest = cmdDest.ExecuteReader();
            int recTotal = 0;
            List<string> tile_ids = new List<string>();
            while (readerDest.Read())
            {
                tile_ids.Add((string)readerDest["tile_id"]);
                this.Dispatcher.Invoke(new Action(() => lblTotalRec.Content = ++recTotal));
                if (cancelTokenSource.IsCancellationRequested)
                {
                    bRtn = false;
                    break;
                }
            }
            readerDest.Close();

            int recCnt = 1;
            foreach (string tile_id in tile_ids)
            {
                cmdDest.CommandText = "SELECT tile_id FROM map WHERE tile_id = '" + tile_id + "'";
                readerDest = cmdDest.ExecuteReader();
                if (readerDest.Read())
                {
                    readerDest.Close();
                }
                else
                {
                    readerDest.Close();
                    cmdDest.CommandText = "INSERT INTO map VALUES(@zoom_level, @tile_column, @tile_row, @tile_id)";
                    SQLiteParameter[] sqlParams =
                    {
                        new SQLiteParameter("@zoom_level", DbType.Int64),
                        new SQLiteParameter("@tile_column", DbType.Int64),
                        new SQLiteParameter("@tile_row", DbType.Int64),
                        new SQLiteParameter("@tile_id", DbType.String),
                    };

                    string[] paramStr = tile_id.Split('-');
                    if (paramStr.Length != 2)
                    {
                        bRtn = false;
                        break;
                    }

                    int len = paramStr[0].Length;
                    string zoom_s = paramStr[0].Substring(0, len - 6).TrimStart('L');
                    if (!long.TryParse(zoom_s, out long zoom_level))
                    {
                        bRtn = false;
                        break;
                    }
                    if (!long.TryParse(paramStr[1], out long tile_column))
                    {
                        bRtn = false;
                        break;
                    }
                    if (long.TryParse(paramStr[0].Substring(len - 6), out long tile_row))
                    {
                        bRtn = false;
                        break;
                    }

                    // tile_row = (1 << (int)zoom_level) - tile_row;  // 如果是水经注老版格式，需要取消注释此行。
                    sqlParams[0].Value = zoom_level;
                    sqlParams[1].Value = tile_column;
                    sqlParams[2].Value = tile_row;
                    sqlParams[3].Value = tile_id;
                    cmdDest.Parameters.AddRange(sqlParams);
                    cmdDest.ExecuteNonQuery();
                    readerDest.Close();
                }
                this.Dispatcher.Invoke(new Action(() => lblCurrentRec.Content = recCnt++));
                progress.Report((double)recCnt * 100.0 / (double)recTotal);
                if (cancelTokenSource.IsCancellationRequested)
                {
                    bRtn = false;
                    break;
                }
            }
            transDest.Commit();
            sqlDest.Close();
            return bRtn;
        }
    }
}
