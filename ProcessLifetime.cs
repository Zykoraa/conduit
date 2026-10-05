using System.ComponentModel;
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.System.JobObjects;

namespace WorkTunnel;

/// <summary>Children inherit the host's job. A host crash closes its last handle and stops both cores.</summary>
internal static unsafe class ProcessLifetime
{
    public static IDisposable OwnHostAndChildren()
    {
        var job = PInvoke.CreateJobObject(null, null);
        if (job.IsInvalid) throw new Win32Exception();
        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!PInvoke.SetInformationJobObject(new Windows.Win32.Foundation.HANDLE(job.DangerousGetHandle()), JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))) throw new Win32Exception();
            using var self = Process.GetCurrentProcess();
            if (!PInvoke.AssignProcessToJobObject(job, self.SafeHandle)) throw new Win32Exception();
            return job;
        }
        catch { job.Dispose(); throw; }
    }
}
