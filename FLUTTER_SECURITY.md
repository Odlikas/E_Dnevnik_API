# Flutter App — Security Guide & API Reference

This document covers everything the Flutter app needs to implement for a fully secure, production-ready
login flow, plus full context about the backend API. Drop this file into the Flutter project root.

---

## Part 1 — API reference

### What the backend is

ASP.NET Core 8 REST API hosted on Heroku. It acts as a secure proxy between the Flutter app and
`ocjene.skole.hr` (Croatian e-Dnevnik school portal). The student's password is **never stored
server-side** — only the resulting e-Dnevnik session cookies, held in memory for 24 hours.

### Base URLs

| Environment | URL |
|-------------|-----|
| Production | `https://your-app.herokuapp.com` (replace with actual Heroku URL) |
| Local dev | `http://localhost:5168` |

### All endpoints

| Method | Path | Auth required | Description |
|--------|------|---------------|-------------|
| `POST` | `/api/Login` | No | Login — returns Bearer token |
| `DELETE` | `/api/Login` | Bearer | Logout — invalidates session server-side |
| `GET` | `/health` | No | Health check — returns `{ "status": "ok" }` |
| `GET` | `/api/Scraper/ScrapeSubjectsAndProfessors` | Bearer | All subjects with professors and average grades |
| `GET` | `/api/Scraper/ScrapeSpecificSubjectGrades?subjectId=XXX` | Bearer | Grades for one subject (subjectId is numeric, from subjects list) |
| `GET` | `/api/Scraper/ScrapeStudentProfile` | Bearer | Name, school, class, class master, program |
| `GET` | `/api/Scraper/ScrapeTests` | Bearer | Upcoming exams grouped by month |
| `GET` | `/api/Scraper/ScrapeNewGrades` | Bearer | Recently entered grades (server-cached, fast on repeat calls) |
| `GET` | `/api/Scraper/ScrapeNewTests` | Bearer | Recently added exams (server-cached, fast on repeat calls) |
| `GET` | `/api/Scraper/ScrapeAbsences` | Bearer | Absences grouped by date |
| `GET` | `/api/Scraper/ScrapeScheduleTable` | Bearer | Weekly timetable |
| `GET` | `/api/Scraper/ScrapeDifferentGrades` | Bearer | Subjects and grades for all past school years (slow — multiple HTTP calls) |
| `GET` | `/api/Scraper/CalculateMissedClassPercentages` | Bearer | % of class hours missed per subject |

### Request/response shapes

**Login:**
```
POST /api/Login
Content-Type: application/json

{ "email": "ime.prezime@skole.hr", "password": "<your-ednevnik-password>" }

→ 200: { "token": "base64encodedtoken==" }
→ 400: "Email i lozinka moraju biti uneseni."
→ 429: "previše neuspjelih pokušaja prijave. pokušaj ponovo za 15 minuta."
→ 500: "csrf token nije pronađen." (e-Dnevnik unreachable)
```

**All scraper requests:**
```
GET /api/Scraper/ScrapeSubjectsAndProfessors
Authorization: Bearer base64encodedtoken==

→ 200: { ...data... }
→ 401: session expired or token invalid — re-login silently
→ 429: rate limited (20 req/min per IP)
→ 5xx: e-Dnevnik down or parsing failed
```

### Error codes — what to do in the app

| Code | Meaning | Action |
|------|---------|--------|
| `200` | Success | Parse and display |
| `400` | Missing/invalid fields | Show validation error |
| `401` | Token expired or invalid | Silent re-login (see §3), then retry |
| `429` | Rate limited or brute-force lockout | Show message, do not retry immediately |
| `500` | Server or scraping error | Show generic error, offer retry |

### Token and session rules

- Token lifetime: **24 hours sliding window** — every successful API call resets the 24h timer
- Brute force: **5 failed login attempts = 15-minute lockout** on that email, returns `429`
- Server restart: all tokens invalidated (Heroku eco dynos restart every 24h) → `401` → re-login
- `ScrapeNewGrades` and `ScrapeNewTests` are fetched live on a cache miss and cached server-side for 10 minutes per session (no background polling) — first call takes ~2s, repeat calls within 10 minutes are instant

---

## Part 2 — Complete login flow implementation

### The full picture

```
First launch                          Subsequent launches
──────────────────────────────        ──────────────────────────────────────
App starts                            App starts
  │                                     │
  ▼                                     ▼
Check secure storage                  Check secure storage
  │                                     │
  ├─ no token → show LoginScreen        ├─ token found → skip LoginScreen
  │                                     │                navigate to HomeScreen
  ▼                                     │
User enters email + password           Mid-session, any API call returns 401
  │                                     │
  ▼                                     ▼
POST /api/Login                       AuthInterceptor catches 401
  │                                     │
  ├─ 200 → store email,                ├─ reads email+password from secure storage
  │         password,                  │
  │         token in                   ├─ POST /api/Login silently
  │         secure storage             │
  │       → navigate HomeScreen        ├─ success → store new token
  │                                    │           → retry original request
  ├─ 429 → show lockout message        │           → user sees nothing
  │                                    │
  └─ other → show error                └─ failure → deleteAll() from storage
                                                  → navigate to LoginScreen
```

---

## §1 — Dependencies to add

```yaml
# pubspec.yaml
dependencies:
  dio: ^5.4.0
  flutter_secure_storage: ^9.0.0
  local_auth: ^2.2.0                    # biometric lock
  flutter_jailbreak_detection: ^1.8.0  # root/jailbreak detection
  flutter_windowmanager: ^0.2.0         # screenshot prevention (Android)
```

---

## §2 — Secure storage setup

**Never use `SharedPreferences` for credentials. Use `flutter_secure_storage`.**
On iOS it writes to Keychain. On Android to EncryptedSharedPreferences. Both are OS-backed
encrypted stores that other apps and filesystem reads cannot access.

```dart
// lib/services/storage_service.dart
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

class StorageService {
  static const _storage = FlutterSecureStorage();

  static const _keyEmail = 'email';
  static const _keyPassword = 'password';
  static const _keyToken = 'bearer_token';

  static Future<void> saveCredentials({
    required String email,
    required String password,
    required String token,
  }) async {
    await _storage.write(key: _keyEmail, value: email);
    await _storage.write(key: _keyPassword, value: password);
    await _storage.write(key: _keyToken, value: token);
  }

  static Future<String?> getToken() => _storage.read(key: _keyToken);
  static Future<String?> getEmail() => _storage.read(key: _keyEmail);
  static Future<String?> getPassword() => _storage.read(key: _keyPassword);

  static Future<void> clearAll() => _storage.deleteAll();
}
```

---

## §3 — Dio client + auth interceptor with mutex

The interceptor automatically:
1. Attaches the Bearer token to every outgoing request
2. On 401: silently re-logs in using stored credentials, stores the new token, retries the request
3. Uses a mutex so if 5 requests fire simultaneously and all get 401, only one re-login happens
   and the others wait — without the mutex they'd all race to re-login at the same time

```dart
// lib/services/api_client.dart
import 'package:dio/dio.dart';
import 'storage_service.dart';

class ApiClient {
  static const String baseUrl = 'https://your-app.herokuapp.com'; // change this

  late final Dio _dio;

  ApiClient() {
    _dio = Dio(BaseOptions(
      baseUrl: baseUrl,
      connectTimeout: const Duration(seconds: 15),
      receiveTimeout: const Duration(seconds: 120), // some endpoints are slow
      contentType: 'application/json',
    ));
    _dio.interceptors.add(_AuthInterceptor(_dio));
  }

  Dio get dio => _dio;
}

class _AuthInterceptor extends Interceptor {
  final Dio _dio;

  _AuthInterceptor(this._dio);

  // mutex state — prevents concurrent re-login races
  bool _isRefreshing = false;
  final List<void Function(String token)> _pendingRequests = [];

  @override
  void onRequest(
    RequestOptions options,
    RequestInterceptorHandler handler,
  ) async {
    final token = await StorageService.getToken();
    if (token != null) {
      options.headers['Authorization'] = 'Bearer $token';
    }
    handler.next(options);
  }

  @override
  void onError(DioException err, ErrorInterceptorHandler handler) async {
    if (err.response?.statusCode != 401) {
      handler.next(err);
      return;
    }

    // another re-login is already in progress — queue this request
    if (_isRefreshing) {
      _pendingRequests.add((newToken) async {
        try {
          err.requestOptions.headers['Authorization'] = 'Bearer $newToken';
          final retried = await _dio.fetch(err.requestOptions);
          handler.resolve(retried);
        } catch (_) {
          handler.next(err);
        }
      });
      return;
    }

    _isRefreshing = true;

    final email = await StorageService.getEmail();
    final password = await StorageService.getPassword();

    if (email == null || password == null) {
      // no credentials stored — user must log in manually
      _isRefreshing = false;
      await StorageService.clearAll();
      handler.next(err);
      return;
    }

    try {
      // silent re-login — user sees nothing
      final response = await _dio.post(
        '/api/Login',
        data: {'email': email, 'password': password},
      );
      final newToken = response.data['token'] as String;
      await StorageService.saveCredentials(
        email: email,
        password: password,
        token: newToken,
      );

      // retry the original request with the new token
      err.requestOptions.headers['Authorization'] = 'Bearer $newToken';
      final retried = await _dio.fetch(err.requestOptions);
      handler.resolve(retried);

      // unblock all queued requests
      for (final pending in _pendingRequests) {
        pending(newToken);
      }
      _pendingRequests.clear();
    } catch (_) {
      // re-login failed — force user back to login screen
      await StorageService.clearAll();
      _pendingRequests.clear();
      handler.next(err);
    } finally {
      _isRefreshing = false;
    }
  }
}
```

---

## §4 — Login screen

```dart
// lib/screens/login_screen.dart
class LoginScreen extends StatefulWidget { ... }

class _LoginScreenState extends State<LoginScreen> {
  final _emailController = TextEditingController();
  final _passwordController = TextEditingController();
  bool _loading = false;
  String? _error;

  Future<void> _login() async {
    setState(() { _loading = true; _error = null; });

    try {
      final response = await ApiClient().dio.post('/api/Login', data: {
        'email': _emailController.text.trim(),
        'password': _passwordController.text,
      });

      final token = response.data['token'] as String;
      await StorageService.saveCredentials(
        email: _emailController.text.trim(),
        password: _passwordController.text,
        token: token,
      );

      if (mounted) {
        Navigator.pushReplacement(
          context,
          MaterialPageRoute(builder: (_) => const HomeScreen()),
        );
      }
    } on DioException catch (e) {
      final status = e.response?.statusCode;
      setState(() {
        _error = status == 429
            ? 'Previše neuspjelih pokušaja. Pričekaj 15 minuta.'
            : 'Prijava nije uspjela. Provjeri email i lozinku.';
      });
    } finally {
      if (mounted) setState(() { _loading = false; });
    }
  }
}
```

---

## §5 — App startup — skip login if token exists

```dart
// lib/main.dart
void main() async {
  WidgetsFlutterBinding.ensureInitialized();
  await _checkDeviceSecurity(); // see §9
  runApp(const App());
}

class App extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      home: FutureBuilder<String?>(
        future: StorageService.getToken(),
        builder: (context, snapshot) {
          if (snapshot.connectionState != ConnectionState.done) {
            return const SplashScreen();
          }
          // token found → go straight to home, interceptor handles 401 if expired
          return snapshot.data != null ? const HomeScreen() : const LoginScreen();
        },
      ),
    );
  }
}
```

---

## §6 — Logout

Must do three things: kill the server session, wipe local storage, clear the nav stack.

```dart
Future<void> logout(Dio dio) async {
  final token = await StorageService.getToken();
  if (token != null) {
    try {
      await dio.delete(
        '/api/Login',
        options: Options(headers: {'Authorization': 'Bearer $token'}),
      );
    } catch (_) {
      // best-effort: even if server call fails, still wipe local storage
    }
  }

  await StorageService.clearAll();

  navigatorKey.currentState?.pushAndRemoveUntil(
    MaterialPageRoute(builder: (_) => const LoginScreen()),
    (_) => false, // clears the entire navigation stack
  );
}
```

---

## §7 — HTTPS only — block all plain HTTP

**In Dio**, always use `https://` in `baseUrl` for production.

**On Android**, add a network security config so the OS rejects any accidental `http://` calls
at the system level — even if there's a bug in your code:

```xml
<!-- android/app/src/main/res/xml/network_security_config.xml (create this file) -->
<?xml version="1.0" encoding="utf-8"?>
<network-security-config>
    <base-config cleartextTrafficPermitted="false" />
</network-security-config>
```

```xml
<!-- android/app/src/main/AndroidManifest.xml — add attribute to <application> -->
<application
    android:networkSecurityConfig="@xml/network_security_config"
    ...>
```

**On iOS**, ATS blocks HTTP by default. Never add `NSAllowsArbitraryLoads` to Info.plist.

---

## §8 — Biometric lock on app resume

Require Face ID / fingerprint when the app comes back from background after 5 minutes idle.
Protects against someone picking up an unlocked phone.

```dart
// lib/services/biometric_service.dart
import 'package:local_auth/local_auth.dart';

class BiometricService {
  static final _auth = LocalAuthentication();
  static DateTime? _lastActiveTime;
  static const _idleTimeout = Duration(minutes: 5);

  static Future<bool> checkOnResume() async {
    final now = DateTime.now();
    final idle = _lastActiveTime == null ||
        now.difference(_lastActiveTime!) > _idleTimeout;

    if (!idle) return true;

    return _auth.authenticate(
      localizedReason: 'Potvrdi identitet za pristup ocjenama',
      options: const AuthenticationOptions(biometricOnly: false),
    );
  }

  static void recordActive() => _lastActiveTime = DateTime.now();
}
```

```dart
// In your root widget
class _AppState extends State<App> with WidgetsBindingObserver {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) async {
    if (state == AppLifecycleState.paused) {
      BiometricService.recordActive();
    }
    if (state == AppLifecycleState.resumed) {
      final ok = await BiometricService.checkOnResume();
      if (!ok) {
        // failed biometric — lock the screen
        navigatorKey.currentState?.push(
          MaterialPageRoute(builder: (_) => const LockScreen()),
        );
      }
    }
  }
}
```

---

## §9 — Jailbreak and root detection

Run this at startup. On a rooted/jailbroken device, OS encryption guarantees that protect
`flutter_secure_storage` can be bypassed.

```dart
import 'package:flutter_jailbreak_detection/flutter_jailbreak_detection.dart';

Future<void> _checkDeviceSecurity() async {
  final isCompromised = await FlutterJailbreakDetection.jailbroken;
  if (isCompromised) {
    // show a warning — you decide whether to block or just warn
    // suggestion: show dialog, let user proceed but make the risk clear
  }
}
```

---

## §10 — Never log credentials or tokens

Search the codebase for `print(` and `debugPrint(` and remove any that output sensitive values.

```dart
// BAD
print('Logging in as $email password=$password');
debugPrint('Token: $token');

// GOOD — log state only, never values
debugPrint('Login successful');
debugPrint('Token refresh completed');
```

If using Firebase Crashlytics or Sentry, make sure your Dio logging interceptor (if any) does
**not** log request headers — `Authorization: Bearer ...` contains the token.

---

## §11 — Screenshot prevention (Android)

Prevents the app appearing in the recents thumbnail and blocks screenshots on grade screens.

```dart
import 'package:flutter_windowmanager/flutter_windowmanager.dart';

// call in initState of screens showing grades
await FlutterWindowManager.addFlags(FlutterWindowManager.FLAG_SECURE);

// call when leaving that screen
await FlutterWindowManager.clearFlags(FlutterWindowManager.FLAG_SECURE);
```

iOS blurs the app in the task switcher automatically — no extra work needed.

---

## §12 — Build obfuscation for release

```bash
# Android APK
flutter build apk --obfuscate --split-debug-info=build/debug-info

# Android App Bundle (Play Store)
flutter build appbundle --obfuscate --split-debug-info=build/debug-info

# iOS
flutter build ipa --obfuscate --split-debug-info=build/debug-info
```

Keep the `build/debug-info` folder. You need it to decode crash report stack traces.

---

## §13 — Certificate pinning (skip for now)

Certificate pinning would block MITM attacks even from rogue certificate authorities.
**Skip this for now** — the API uses Heroku + Let's Encrypt which auto-renews every 90 days.
Pinning the leaf certificate would break the app every 90 days. Revisit this if you move to
a dedicated domain with a manually managed certificate.

---

## Summary — implementation order

| Priority | Item | Section |
|----------|------|---------|
| **Must — do first** | Add `flutter_secure_storage`, implement `StorageService` | §2 |
| **Must** | Set up Dio client + `AuthInterceptor` with mutex | §3 |
| **Must** | Login screen with 429 handling | §4 |
| **Must** | App startup: skip login if token stored | §5 |
| **Must** | Proper logout: server-side + wipe storage + clear nav stack | §6 |
| **Must** | HTTPS only + Android cleartext block | §7 |
| **Must** | Remove all credential/token logging | §10 |
| Recommended | Biometric lock on resume after 5 min idle | §8 |
| Recommended | Jailbreak/root detection at startup | §9 |
| Recommended | Build obfuscation on release | §12 |
| Optional | Screenshot prevention on grade screens | §11 |
| Later | Certificate pinning (after leaving Heroku/Let's Encrypt) | §13 |
