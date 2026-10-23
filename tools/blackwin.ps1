Add-Type -AssemblyName PresentationFramework
$w = New-Object System.Windows.Window
$w.WindowStyle = 'None'
$w.ResizeMode = 'NoResize'
$w.Background = [System.Windows.Media.Brushes]::Black
$w.WindowState = 'Maximized'
$w.Topmost = $true
$w.ShowInTaskbar = $false
$t = New-Object System.Windows.Threading.DispatcherTimer
$t.Interval = [TimeSpan]::FromSeconds(7)
$t.Add_Tick({ $w.Close() })
$t.Start()
$w.ShowDialog() | Out-Null
