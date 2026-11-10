import { useCallback, useEffect, useState } from 'react';
import axios from 'axios';

const API_BASE = 'http://localhost:8080';
const STUDENT_ID_STORAGE_KEY = 'schoolAdmission.studentId';
const ADMIN_EMAIL = 'adminpanel@gmail.com';

function formatDate(value) {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short'
  }).format(new Date(value));
}

export default function App() {
  const [studentId, setStudentId] = useState(() => {
    const savedStudentId = window.localStorage.getItem(STUDENT_ID_STORAGE_KEY) || '';
    return /^\d{17}$/.test(savedStudentId) ? savedStudentId : '';
  });
  const [classes, setClasses] = useState([]);
  const [myHolds, setMyHolds] = useState([]);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');
  const [applications, setApplications] = useState([]);
  const [dashboardError, setDashboardError] = useState('');
  const [isDashboardLoading, setIsDashboardLoading] = useState(true);
  const [lastUpdated, setLastUpdated] = useState(null);
  const [isAdminModalOpen, setIsAdminModalOpen] = useState(false);
  const [adminError, setAdminError] = useState('');
  const [loginEmail, setLoginEmail] = useState(ADMIN_EMAIL);
  const [loginPassword, setLoginPassword] = useState('');
  const [adminEmail, setAdminEmail] = useState('');
  const [isAdminAuthenticated, setIsAdminAuthenticated] = useState(false);
  const [isCheckingAdminSession, setIsCheckingAdminSession] = useState(true);
  const [isLoggingIn, setIsLoggingIn] = useState(false);

  useEffect(() => {
    if (/^\d{17}$/.test(studentId)) {
      window.localStorage.setItem(STUDENT_ID_STORAGE_KEY, studentId);
    } else {
      window.localStorage.removeItem(STUDENT_ID_STORAGE_KEY);
    }
  }, [studentId]);

  const fetchClasses = useCallback(async () => {
    try {
      const { data } = await axios.get(`${API_BASE}/classes`);
      setClasses(data);
    } catch (requestError) {
      console.error('Failed to fetch classes', requestError);
      setError('Could not load classes. Please check your connection and try again.');
    }
  }, []);

  const fetchMyHolds = useCallback(async () => {
    if (studentId.length !== 17) {
      setMyHolds([]);
      return;
    }

    try {
      const { data } = await axios.get(`${API_BASE}/me/holds`, {
        headers: { 'X-Student-Id': studentId }
      });
      setMyHolds(data);
    } catch (requestError) {
      console.error('Failed to fetch my holds', requestError);
      setError('Could not load your applications. Please try again.');
    }
  }, [studentId]);

  const fetchAdminApplications = useCallback(async () => {
    setDashboardError('');
    try {
      const { data } = await axios.get(`${API_BASE}/admin/overview`, {
        withCredentials: true
      });
      setApplications(data);
      setLastUpdated(new Date());
      setIsDashboardLoading(false);
    } catch (requestError) {
      if (requestError.response?.status === 401) {
        setIsAdminAuthenticated(false);
        setAdminEmail('');
        setDashboardError('Your admin session has expired. Please sign in again.');
      } else {
        console.error('Failed to fetch admin applications', requestError);
        setDashboardError('Could not load applications. Please retry in a moment.');
      }
      setIsDashboardLoading(false);
    }
  }, []);

  useEffect(() => {
    if (isAdminAuthenticated || isCheckingAdminSession) return undefined;

    const refreshStudentData = () => {
      fetchClasses();
      fetchMyHolds();
    };

    refreshStudentData();
    const intervalId = window.setInterval(refreshStudentData, 3000);
    return () => window.clearInterval(intervalId);
  }, [fetchClasses, fetchMyHolds, isAdminAuthenticated, isCheckingAdminSession]);

  useEffect(() => {
    let active = true;
    axios.get(`${API_BASE}/admin/session`, { withCredentials: true })
      .then(({ data }) => {
        if (active) {
          setAdminEmail(data.email);
          setIsAdminAuthenticated(true);
          fetchAdminApplications();
        }
      })
      .catch(() => {
        if (active) setIsAdminAuthenticated(false);
      })
      .finally(() => {
        if (active) setIsCheckingAdminSession(false);
      });

    return () => {
      active = false;
    };
  }, [fetchAdminApplications]);

  useEffect(() => {
    if (!isAdminAuthenticated) return undefined;

    const intervalId = window.setInterval(fetchAdminApplications, 15000);
    return () => window.clearInterval(intervalId);
  }, [fetchAdminApplications, isAdminAuthenticated]);

  useEffect(() => {
    if (!isAdminModalOpen) return undefined;

    const handleKeyDown = (event) => {
      if (event.key === 'Escape' && !isLoggingIn) setIsAdminModalOpen(false);
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isAdminModalOpen, isLoggingIn]);

  const openAdminLogin = () => {
    setAdminError('');
    setIsAdminModalOpen(true);
  };

  const handleAdminLogin = async (event) => {
    event.preventDefault();
    setAdminError('');
    setIsLoggingIn(true);

    try {
      const { data } = await axios.post(
        `${API_BASE}/admin/login`,
        { email: loginEmail.trim(), password: loginPassword },
        { withCredentials: true }
      );
      setAdminEmail(data.email);
      setIsDashboardLoading(true);
      setIsAdminAuthenticated(true);
      fetchAdminApplications();
      setLoginPassword('');
      setIsAdminModalOpen(false);
    } catch (requestError) {
      setAdminError(requestError.response?.status === 401
        ? 'The admin email or password is incorrect.'
        : 'Could not log in. Check that the backend is running and try again.');
    } finally {
      setIsLoggingIn(false);
    }
  };

  const handleAdminLogout = async () => {
    try {
      await axios.post(`${API_BASE}/admin/logout`, {}, { withCredentials: true });
    } catch (requestError) {
      if (requestError.response?.status !== 401) {
        setDashboardError('Could not log out. Please try again.');
        return;
      }
    }

    setIsAdminAuthenticated(false);
    setAdminEmail('');
    setApplications([]);
    setDashboardError('');
  };

  const handleApply = async (classId) => {
    setError('');
    setSuccess('');
    if (!/^\d{17}$/.test(studentId)) {
      setError('Please enter a valid 17-digit Birth Registration Number.');
      return;
    }

    try {
      await axios.post(`${API_BASE}/classes/${classId}/holds`, {
        studentBirthRegistrationId: studentId
      });
      setSuccess('Seat held successfully. Confirm your admission before the hold expires.');
      fetchClasses();
      fetchMyHolds();
    } catch (requestError) {
      setError(requestError.response?.data?.message || 'Failed to hold seat.');
    }
  };

  const handleConfirm = async (holdId) => {
    setError('');
    setSuccess('');
    try {
      await axios.post(`${API_BASE}/holds/${holdId}/confirm`, {}, {
        headers: { 'X-Student-Id': studentId }
      });
      setSuccess('Admission confirmed successfully.');
      fetchClasses();
      fetchMyHolds();
    } catch (requestError) {
      setError(requestError.response?.data?.message || 'Failed to confirm payment.');
    }
  };

  const handleCancel = async (holdId) => {
    setError('');
    setSuccess('');
    try {
      await axios.delete(`${API_BASE}/holds/${holdId}`, {
        headers: { 'X-Student-Id': studentId }
      });
      setSuccess('Application cancelled.');
      fetchClasses();
      fetchMyHolds();
    } catch (requestError) {
      setError(requestError.response?.data?.message || 'Failed to cancel application.');
    }
  };

  const classGroups = applications.reduce((groups, application) => {
    const group = groups.find(item => item.classId === application.classId);
    if (group) {
      group.applications.push(application);
    } else {
      groups.push({
        classId: application.classId,
        className: application.className,
        applications: [application]
      });
    }
    return groups;
  }, []);
  const confirmedCount = applications.filter(application => application.status === 'CONFIRMED').length;
  const heldCount = applications.filter(application => application.status === 'HELD').length;

  if (isCheckingAdminSession) {
    return (
      <main className="session-loading" aria-live="polite">
        <span className="loading-spinner" aria-hidden="true" />
        <p>Loading admission portal…</p>
      </main>
    );
  }

  return (
    <div className={isAdminAuthenticated ? 'app-shell admin-app-shell' : 'app-shell'}>
      {isAdminAuthenticated ? (
        <>
          <header className="admin-topbar">
            <a className="brand-lockup" href="/" aria-label="School Admission System home">
              <span className="brand-mark" aria-hidden="true">SA</span>
              <span className="brand-copy">
                <strong>School Admission</strong>
                <small>Administration</small>
              </span>
            </a>
            <div className="admin-user">
              <span className="admin-avatar" aria-hidden="true">
                {adminEmail.slice(0, 1).toUpperCase()}
              </span>
              <span className="admin-user-email">{adminEmail}</span>
              <button className="logout-button" onClick={handleAdminLogout} type="button">
                Log out
              </button>
            </div>
          </header>

          <main className="admin-dashboard">
            <section className="dashboard-heading">
              <div>
                <p className="eyebrow">Admissions management</p>
                <h1>Applications</h1>
              </div>
              <div className="live-indicator">
                <span aria-hidden="true" />
                Live updates
              </div>
            </section>

            <section className="application-summary" aria-label="Application totals">
              <article className="summary-card">
                <span className="summary-icon summary-icon-confirmed" aria-hidden="true">✓</span>
                <div>
                  <p>Confirmed admissions</p>
                  <strong>{confirmedCount}</strong>
                </div>
              </article>
              <article className="summary-card">
                <span className="summary-icon summary-icon-held" aria-hidden="true">◷</span>
                <div>
                  <p>Active holds</p>
                  <strong>{heldCount}</strong>
                </div>
              </article>
              <article className="summary-card">
                <span className="summary-icon summary-icon-classes" aria-hidden="true">▦</span>
                <div>
                  <p>Classes with applications</p>
                  <strong>{classGroups.length}</strong>
                </div>
              </article>
            </section>

            <section className="applications-section" aria-labelledby="applications-title">
              <div className="section-heading">
                <div>
                  <h2 id="applications-title">Class-wise applications</h2>
                  {lastUpdated && (
                    <p className="last-updated">Updated {lastUpdated.toLocaleTimeString()}</p>
                  )}
                </div>
                <button
                  className="refresh-button"
                  disabled={isDashboardLoading}
                  onClick={() => {
                    setIsDashboardLoading(true);
                    fetchAdminApplications();
                  }}
                  type="button"
                >
                  <span aria-hidden="true">↻</span>
                  Refresh
                </button>
              </div>

              {dashboardError && (
                <div className="dashboard-alert" role="alert">
                  <p>{dashboardError}</p>
                  {!isAdminAuthenticated && (
                    <button className="text-button" onClick={openAdminLogin} type="button">
                      Sign in again
                    </button>
                  )}
                </div>
              )}

              {isDashboardLoading && applications.length === 0 ? (
                <div className="dashboard-empty">
                  <span className="loading-spinner" aria-hidden="true" />
                  <p>Loading applications…</p>
                </div>
              ) : classGroups.length === 0 ? (
                <div className="dashboard-empty">
                  <span className="empty-icon" aria-hidden="true">▤</span>
                  <h3>No active applications</h3>
                </div>
              ) : (
                <div className="class-groups">
                  {classGroups.map(group => (
                    <article className="class-card" key={group.classId}>
                      <header className="class-card-header">
                        <div>
                          <span className="class-card-label">Class</span>
                          <h3>{group.className}</h3>
                        </div>
                        <span className="application-count">
                          {group.applications.length} {group.applications.length === 1 ? 'application' : 'applications'}
                        </span>
                      </header>
                      <div className="table-scroll">
                        <table className="applications-table">
                          <thead>
                            <tr>
                              <th scope="col">Birth Registration ID</th>
                              <th scope="col">Status</th>
                              <th scope="col">Hold expires</th>
                            </tr>
                          </thead>
                          <tbody>
                            {group.applications.map(application => (
                              <tr key={application.holdId}>
                                <td className="student-id-cell">{application.studentId}</td>
                                <td>
                                  <span className={`status-badge status-${application.status.toLowerCase()}`}>
                                    <span aria-hidden="true" />
                                    {application.status === 'CONFIRMED' ? 'Confirmed' : 'Held'}
                                  </span>
                                </td>
                                <td className="expiry-cell">
                                  {application.status === 'HELD' ? formatDate(application.expiresAt) : '—'}
                                </td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </div>
                    </article>
                  ))}
                </div>
              )}
            </section>
          </main>
        </>
      ) : (
        <>
          <header className="student-topbar">
            <a className="brand-lockup" href="/" aria-label="School Admission System home">
              <span className="brand-mark" aria-hidden="true">SA</span>
              <span className="brand-copy">
                <strong>School Admission</strong>
                <small>Student portal</small>
              </span>
            </a>
            <button className="admin-login-button" onClick={openAdminLogin} type="button">
              Admin login
            </button>
          </header>

          <main className="student-content">
            <section className="student-hero">
              <p className="eyebrow">Welcome</p>
              <h1>Find your next class.</h1>
            </section>

            {error && <div className="student-alert student-alert-error" role="alert">{error}</div>}
            {success && <div className="student-alert student-alert-success" role="status">{success}</div>}

            <section className="student-id-panel">
              <label htmlFor="student-id">Birth Registration ID</label>
              <input
                autoComplete="off"
                id="student-id"
                inputMode="numeric"
                maxLength={17}
                onChange={event => setStudentId(event.target.value.replace(/\D/g, ''))}
                placeholder="Enter your 17-digit ID"
                value={studentId}
              />
            </section>

            <section className="student-section" aria-labelledby="available-classes-title">
              <div className="student-section-heading">
                <div>
                  <p className="eyebrow">Choose a class</p>
                  <h2 id="available-classes-title">Available classes</h2>
                </div>
              </div>
              <div className="table-scroll student-table-wrap">
                <table className="applications-table student-classes-table">
                  <thead>
                    <tr>
                      <th scope="col">Class</th>
                      <th scope="col">Total seats</th>
                      <th scope="col">Held</th>
                      <th scope="col">Admitted</th>
                      <th scope="col">Available</th>
                      <th scope="col"><span className="visually-hidden">Action</span></th>
                    </tr>
                  </thead>
                  <tbody>
                    {classes.map(classItem => (
                      <tr key={classItem.id}>
                        <td className="class-name-cell">{classItem.name}</td>
                        <td>{classItem.totalSeats}</td>
                        <td>{classItem.heldSeats}</td>
                        <td>{classItem.confirmedSeats}</td>
                        <td>{classItem.availableSeats}</td>
                        <td>
                          <button
                            className="apply-button"
                            disabled={classItem.availableSeats <= 0}
                            onClick={() => handleApply(classItem.id)}
                            type="button"
                          >
                            Apply
                          </button>
                        </td>
                      </tr>
                    ))}
                    {classes.length === 0 && (
                      <tr><td className="empty-table-cell" colSpan="6">No classes are available right now.</td></tr>
                    )}
                  </tbody>
                </table>
              </div>
            </section>

            <section className="student-section my-applications" aria-labelledby="my-applications-title">
              <div className="student-section-heading">
                <div>
                  <p className="eyebrow">Your progress</p>
                  <h2 id="my-applications-title">My applications</h2>
                </div>
              </div>
              {!/^\d{17}$/.test(studentId) ? (
                <div className="student-empty">Your applications will appear here.</div>
              ) : (
                <div className="table-scroll student-table-wrap">
                  <table className="applications-table">
                    <thead>
                      <tr>
                        <th scope="col">Class</th>
                        <th scope="col">Status</th>
                        <th scope="col">Hold expires</th>
                        <th scope="col">Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {myHolds.map(hold => {
                        const expired = new Date() > new Date(hold.expiresAt);
                        const status = expired && hold.status === 'HELD' ? 'EXPIRED' : hold.status;

                        return (
                          <tr key={hold.holdId}>
                            <td className="class-name-cell">{hold.className}</td>
                            <td>{status}</td>
                            <td>{hold.status === 'HELD' ? formatDate(hold.expiresAt) : '—'}</td>
                            <td className="student-row-actions">
                              {status === 'HELD' && !expired && (
                                <>
                                  <button className="confirm-button" onClick={() => handleConfirm(hold.holdId)} type="button">
                                    Confirm admission
                                  </button>
                                  <button className="cancel-application-button" onClick={() => handleCancel(hold.holdId)} type="button">
                                    Cancel
                                  </button>
                                </>
                              )}
                            </td>
                          </tr>
                        );
                      })}
                      {myHolds.length === 0 && (
                        <tr><td className="empty-table-cell" colSpan="4">No applications found for this ID.</td></tr>
                      )}
                    </tbody>
                  </table>
                </div>
              )}
            </section>
          </main>
        </>
      )}

      {isAdminModalOpen && !isAdminAuthenticated && (
        <div
          className="admin-modal-backdrop"
          onMouseDown={event => {
            if (event.target === event.currentTarget && !isLoggingIn) setIsAdminModalOpen(false);
          }}
        >
          <section aria-labelledby="admin-heading" aria-modal="true" className="admin-modal" role="dialog">
            <div className="admin-modal-header">
              <div className="modal-brand-mark" aria-hidden="true">SA</div>
              <button
                aria-label="Close admin login"
                className="admin-modal-close"
                disabled={isLoggingIn}
                onClick={() => setIsAdminModalOpen(false)}
                type="button"
              >
                ×
              </button>
            </div>
            <p className="eyebrow">Secure access</p>
            <h2 id="admin-heading">Admin sign in</h2>
            <p className="admin-modal-description">Sign in to review admission holds and confirmed applications.</p>
            <form className="admin-login-form" onSubmit={handleAdminLogin}>
              <label htmlFor="admin-email">Email address</label>
              <input
                autoComplete="username"
                autoFocus
                id="admin-email"
                onChange={event => setLoginEmail(event.target.value)}
                required
                type="email"
                value={loginEmail}
              />
              <label htmlFor="admin-password">Password</label>
              <input
                autoComplete="current-password"
                id="admin-password"
                onChange={event => setLoginPassword(event.target.value)}
                required
                type="password"
                value={loginPassword}
              />
              {adminError && <p className="admin-form-error" role="alert">{adminError}</p>}
              <button className="sign-in-button" disabled={isLoggingIn} type="submit">
                {isLoggingIn ? 'Signing in…' : 'Sign in'}
              </button>
            </form>
          </section>
        </div>
      )}
    </div>
  );
}
