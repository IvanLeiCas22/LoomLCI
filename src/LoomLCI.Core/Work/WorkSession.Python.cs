using LoomLCI.Core.Python;

namespace LoomLCI.Core.Work;

public sealed partial class WorkSession
{
    private PythonPackageEnvironment? _pythonPackageEnvironment;

    internal LoomResult<PythonPackageEnvironment?>
        GetPythonPackageEnvironment()
    {
        lock (_stateGate)
        {
            if (_state != WorkSessionState.Active)
            {
                return LoomResult<PythonPackageEnvironment?>.Failure(
                    UnavailableError());
            }

            return LoomResult<PythonPackageEnvironment?>.Success(
                _pythonPackageEnvironment);
        }
    }

    internal LoomResult<PythonPackageEnvironment?>
        SetPythonPackageEnvironment(
            PythonPackageEnvironment? environment)
    {
        lock (_stateGate)
        {
            if (_state != WorkSessionState.Active)
            {
                return LoomResult<PythonPackageEnvironment?>.Failure(
                    UnavailableError());
            }

            _pythonPackageEnvironment = environment;
            return LoomResult<PythonPackageEnvironment?>.Success(
                _pythonPackageEnvironment);
        }
    }

    private void ClearPythonPackageEnvironmentUnsafe()
        => _pythonPackageEnvironment = null;
}
