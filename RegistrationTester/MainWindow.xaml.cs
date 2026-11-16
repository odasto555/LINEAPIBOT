using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RegistrationTester;

public partial class MainWindow : Window
{
    private static readonly Dictionary<string, string> States = new()
    {
        ["queued"] = "อยู่ในคิว", ["running"] = "กำลังทำงาน", ["awaitingOtp"] = "รอ OTP",
        ["succeeded"] = "สำเร็จ", ["failed"] = "ล้มเหลว", ["expired"] = "หมดเวลา", ["interrupted"] = "หยุดกลางทาง"
    };

    private static readonly Dictionary<string, string> Stages = new()
    {
        ["openSession"] = "เปิดเซสชันสมัคร", ["checkRegistrationMethod"] = "ตรวจวิธีสมัคร",
        ["requestOtp"] = "ขอรหัส OTP", ["awaitingOtp"] = "รอ OTP จากผู้ใช้", ["verifyOtp"] = "ตรวจ OTP",
        ["validateProfile"] = "ตรวจชื่อโปรไฟล์", ["setPassword"] = "ตั้งรหัสผ่าน LINE",
        ["registerAccount"] = "สร้างบัญชี LINE", ["done"] = "เสร็จสิ้น"
    };

    private readonly RegistrationApi _api;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly CancellationTokenSource _lifetime = new();
    private ConsoleSession? _session;
    private Guid? _selectedJob;
    private Guid? _requestId;
    private bool _accepted;
    private bool _submitting;
    private bool _refreshing;
    private bool _updatingPicker;

    internal MainWindow(RegistrationApi api)
    {
        _api = api;
        InitializeComponent();
        ServerLabel.Text = api.BaseAddress.GetLeftPart(UriPartial.Authority);
        _poll.Tick += async (_, _) => await RefreshJobsAsync();
        Loaded += (_, _) => UsernameInput.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _poll.Stop();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _api.Dispose();
        base.OnClosed(e);
    }

    private async void Login(object sender, RoutedEventArgs e)
    {
        var username = UsernameInput.Text.Trim();
        var password = LoginPasswordInput.Password;
        if (username.Length == 0 || password.Length == 0)
        {
            LoginMessage.Text = "กรอกชื่อผู้ใช้และรหัสผ่าน";
            return;
        }

        LoginButton.IsEnabled = false;
        try
        {
            await RunAsync(async () =>
            {
                var session = await _api.LoginAsync(username, password, _lifetime.Token);
                SetSession(session);
                await RefreshJobsAsync();
            }, LoginMessage);
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private async void Logout(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session)
            return;

        LogoutButton.IsEnabled = false;
        try
        {
            await RunAsync(async () =>
            {
                await _api.LogoutAsync(session, _lifetime.Token);
                SetSession(null);
            }, SyncMessage, session);
        }
        finally
        {
            LogoutButton.IsEnabled = true;
        }
    }

    private void OpenMonitor(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_api.MonitorUrl.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            SyncMessage.Text = $"เปิดเบราว์เซอร์ไม่สำเร็จ เข้า Monitor ได้ที่ {_api.MonitorUrl}";
        }
    }

    private void SetSession(ConsoleSession? session)
    {
        _session = session;
        _selectedJob = null;
        LoginPasswordInput.Clear();
        LoginMessage.Text = string.Empty;
        SyncMessage.Text = string.Empty;
        OtpMessage.Text = string.Empty;
        OtpInput.Clear();
        SetJobs([]);
        RenderJob(null);
        LineLogOutput.Text = "เลือกงานเพื่อดู log";

        LoginPanel.Visibility = session is null ? Visibility.Visible : Visibility.Collapsed;
        Workspace.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        SessionActions.Visibility = Workspace.Visibility;
        SignedInUser.Text = session?.Username ?? string.Empty;
        MonitorButton.Visibility = session?.Role == "admin" ? Visibility.Visible : Visibility.Collapsed;

        if (session is null)
        {
            _poll.Stop();
            LinePasswordInput.Clear();
            DeviceIdInput.Clear();
            DeviceModelInput.Clear();
            UsernameInput.Focus();
            return;
        }

        ResetRegistration();
        _poll.Start();
        PhoneInput.Focus();
    }

    private void GenerateNew(object sender, RoutedEventArgs e)
    {
        if (_accepted)
            ResetRegistration();
        else
            GenerateDefaults();
    }

    private void ResetRegistration()
    {
        _accepted = false;
        PhoneInput.Clear();
        CountryInput.Text = "TH";
        DisplayNameInput.Clear();
        GenerateDefaults();
        UpdateRegistrationControls();
    }

    private void GenerateDefaults()
    {
        var defaults = RegistrationDefaults.Generate();
        LinePasswordInput.Password = defaults.Password;
        DeviceIdInput.Text = defaults.DeviceId;
        DeviceModelInput.Text = defaults.DeviceModel;
        RegistrationMessage.Text = string.Empty;
        _requestId = null;
    }

    private void RegistrationEdited(object sender, RoutedEventArgs e) => _requestId = null;

    private void CopyPassword(object sender, RoutedEventArgs e)
    {
        if (LinePasswordInput.Password.Length == 0)
            return;

        try
        {
            Clipboard.SetText(LinePasswordInput.Password);
            RegistrationMessage.Text = "คัดลอกรหัสผ่านบัญชี LINE แล้ว";
        }
        catch (ExternalException)
        {
            RegistrationMessage.Text = "คัดลอกไม่ได้ กรุณาลองอีกครั้ง";
        }
    }

    private async void SubmitRegistration(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session || _submitting || _accepted)
            return;

        var phone = PhoneInput.Text.Trim();
        var country = CountryInput.Text.Trim().ToUpperInvariant();
        var display = DisplayNameInput.Text.Trim();
        if (phone.Length is < 8 or > 16 || phone[0] != '+' || phone[1] is < '1' or > '9' ||
            phone.AsSpan(1).IndexOfAnyExcept("0123456789") >= 0)
        {
            RegistrationMessage.Text = "กรอกหมายเลขในรูปแบบ +66812345678";
            return;
        }

        if (country.Length != 2 || !country.All(char.IsAsciiLetter) ||
            display.Length == 0 || string.IsNullOrWhiteSpace(LinePasswordInput.Password))
        {
            RegistrationMessage.Text = "กรอกรหัสประเทศ 2 ตัวอักษร ชื่อที่แสดง และรหัสผ่าน LINE";
            return;
        }

        var request = new RegistrationRequest(phone, country, display, LinePasswordInput.Password,
            DeviceIdInput.Text, DeviceModelInput.Text);
        var key = _requestId ??= Guid.NewGuid();
        _submitting = true;
        UpdateRegistrationControls();
        try
        {
            await RunAsync(async () =>
            {
                var accepted = await _api.RegisterAsync(session, request, key, _lifetime.Token);
                if (_session != session)
                    return;

                _accepted = true;
                StartTracking(accepted.JobId);
                RegistrationMessage.Text = "รับงานเข้าคิวแล้ว";
                await RefreshJobsAsync();
            }, RegistrationMessage, session);
        }
        finally
        {
            _submitting = false;
            UpdateRegistrationControls();
        }
    }

    private void UpdateRegistrationControls()
    {
        RegistrationFields.IsEnabled = !_accepted && !_submitting;
        SubmitButton.IsEnabled = RegistrationFields.IsEnabled;
        GenerateButton.IsEnabled = !_submitting;
        GenerateButton.Content = _accepted ? "เริ่มงานใหม่" : "สุ่มค่าใหม่";
    }

    private void SelectJob(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingPicker && JobPicker.SelectedItem is RegistrationJob job)
        {
            StartTracking(job.Id);
            RenderJob(job);
        }
    }

    private void StartTracking(Guid id)
    {
        if (_selectedJob == id)
            return;

        _selectedJob = id;
        OtpInput.Clear();
        OtpMessage.Text = string.Empty;
        RenderJob(null);
        LineLogOutput.Text = "กำลังโหลด log";
    }

    private async void SubmitOtp(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session || _selectedJob is not { } jobId)
            return;

        var code = OtpInput.Text.Trim();
        if (code.Length is < 4 or > 12 || !code.All(char.IsAsciiDigit))
        {
            OtpMessage.Text = "กรอกตัวเลข OTP 4–12 หลัก";
            return;
        }

        SendOtpButton.IsEnabled = false;
        try
        {
            await RunAsync(async () =>
            {
                await _api.SubmitOtpAsync(session, jobId, code, _lifetime.Token);
                if (_session == session && _selectedJob == jobId)
                {
                    OtpInput.Clear();
                    OtpMessage.Text = "ส่ง OTP แล้ว";
                }
                await RefreshJobsAsync();
            }, OtpMessage, session);
        }
        finally
        {
            SendOtpButton.IsEnabled = true;
        }
    }

    private async Task RefreshJobsAsync()
    {
        if (_session is not { } session || _refreshing || JobPicker.IsDropDownOpen)
            return;

        _refreshing = true;
        try
        {
            await RunAsync(async () =>
            {
                var jobs = (await _api.JobsAsync(session, _lifetime.Token))
                    .Where(job => job.Kind == "registration").ToList();
                if (_session != session)
                    return;

                if (_selectedJob is { } id && jobs.All(job => job.Id != id))
                    jobs.Insert(0, await _api.JobAsync(session, id, _lifetime.Token));
                if (_session != session)
                    return;

                _selectedJob ??= jobs.FirstOrDefault()?.Id;
                SetJobs(jobs);
                RenderJob(jobs.FirstOrDefault(job => job.Id == _selectedJob));
                SyncMessage.Text = $"อัปเดต {DateTime.Now:HH:mm:ss}";
                if (_selectedJob is { } jobId)
                    await RefreshLineLogAsync(session, jobId);
            }, SyncMessage, session);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void SetJobs(IReadOnlyList<RegistrationJob> jobs)
    {
        _updatingPicker = true;
        JobPicker.ItemsSource = jobs;
        JobPicker.SelectedItem = jobs.FirstOrDefault(job => job.Id == _selectedJob);
        _updatingPicker = false;
    }

    private async Task RefreshLineLogAsync(ConsoleSession session, Guid jobId)
    {
        string text;
        try
        {
            var entries = await _api.LineLogAsync(session, jobId, _lifetime.Token);
            text = entries.Length == 0 ? "ยังไม่มี log สำหรับงานนี้" : string.Join(Environment.NewLine, entries.Select(FormatLineLog));
        }
        catch (ApiRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            text = "ไม่พบ log สำหรับงานนี้ ตรวจว่า API บน Render เป็นเวอร์ชันล่าสุด";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            text = "โหลด log ไม่สำเร็จ";
        }

        if (_session == session && _selectedJob == jobId && LineLogOutput.Text != text)
        {
            var atEnd = LineLogOutput.VerticalOffset >= LineLogOutput.ExtentHeight - LineLogOutput.ViewportHeight - 1;
            LineLogOutput.Text = text;
            if (atEnd)
                LineLogOutput.ScrollToEnd();
        }
    }

    private static string FormatLineLog(LineLogEntry entry)
    {
        var summary = string.Join("  ", new[]
        {
            entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"),
            entry.Phase switch { "send" => "SEND", "receive" => "RECV", _ => "INFO" },
            entry.Operation,
            entry.Phase == "send" ? $"POST {entry.Host}{entry.Path}" : null,
            entry.HttpStatus is { } http ? $"HTTP {http}" : null,
            entry.RpcCode is { } rpc ? $"RPC {rpc}" : null,
            entry.Outcome,
            entry.ElapsedMilliseconds is { } elapsed ? $"{elapsed} ms" : null
        }.Where(value => !string.IsNullOrEmpty(value)));
        if (entry.PayloadHex is null)
            return summary;

        var direction = entry.Phase == "send" ? "REQ" : "RES";
        return $"{summary}{Environment.NewLine}    HEX {direction} ({entry.PayloadBytes} B)  {entry.PayloadHex}";
    }

    private void RenderJob(RegistrationJob? job)
    {
        JobStateLabel.Text = job is null
            ? (_selectedJob is null ? "ยังไม่ได้เลือกงาน" : "กำลังโหลดสถานะ")
            : States.GetValueOrDefault(job.State, job.State);
        JobStageLabel.Text = job is null ? string.Empty : Stages.GetValueOrDefault(job.Stage, job.Stage);
        JobIdLabel.Text = job?.Id.ToString() ?? string.Empty;
        JobResultLabel.Text = job?.Result?.AccountId is { } account ? $"บัญชี LINE {account}" : string.Empty;
        JobErrorLabel.Text = job?.Error?.Code ?? string.Empty;
        OtpPanel.Visibility = job?.State == "awaitingOtp" ? Visibility.Visible : Visibility.Collapsed;
        OtpDeadlineLabel.Text = job?.OtpExpiresAt is { } time ? $"ส่งก่อน {time.ToLocalTime():HH:mm:ss}" : string.Empty;
    }

    private async Task RunAsync(Func<Task> operation, TextBlock message, ConsoleSession? session = null)
    {
        message.Text = string.Empty;
        try
        {
            await operation();
        }
        catch (ApiRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized && session is not null)
        {
            if (_session == session)
            {
                SetSession(null);
                LoginMessage.Text = "เซสชันหมดอายุ กรุณาเข้าสู่ระบบอีกครั้ง";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is ApiRequestException or HttpRequestException or JsonException or OperationCanceledException)
        {
            if (session is null || _session == session)
                message.Text = ex is ApiRequestException ? ex.Message : "ติดต่อ API ไม่สำเร็จ กรุณาลองอีกครั้ง";
        }
    }
}

