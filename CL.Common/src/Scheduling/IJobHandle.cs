using CL.Common.Errors;
using CL.Common.Parser;
using CodeLogic.Core.Results;

namespace CL.Common.Scheduling;

/// <summary>A scheduled job. Disposing it unschedules the job.</summary>
public interface IJobHandle : IDisposable
{
    /// <summary>The job's name.</summary>
    string Name { get; }

    /// <summary>Skips scheduled runs until <see cref="Resume"/> (manual triggers still run).</summary>
    void Pause();

    /// <summary>Resumes scheduled runs from the next due time.</summary>
    void Resume();
}
