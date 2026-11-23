package com.lengjiao.helper;

import android.app.Notification;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.drawable.BitmapDrawable;
import android.graphics.drawable.Drawable;
import android.media.MediaMetadata;
import android.media.session.MediaController;
import android.media.session.MediaSessionManager;
import android.media.session.PlaybackState;
import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.net.Uri;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.service.notification.NotificationListenerService;
import android.service.notification.StatusBarNotification;
import android.util.Base64;
import android.util.Log;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.ByteArrayOutputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.OutputStreamWriter;
import java.io.PrintWriter;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;

/**
 * 棱角互联手机端助手。
 * 协议 v2：一行一个 JSON（UTF-8，\n 分帧），与 PC 端 HelperClient 一一对应。
 * 注意：这里全部用匿名类而不是 lambda —— 编译时用 android.jar 当 bootclasspath，
 * java.lang.invoke.LambdaMetafactory 不在里面，lambda 会直接编译失败。
 */
public class HelperService extends NotificationListenerService {

    private static final String TAG = "LjcHelper";
    private static final String SOCKET_NAME = "lengjiao_helper";

    private final Handler main = new Handler(Looper.getMainLooper());
    private volatile PrintWriter writer;
    private volatile boolean running;
    private Thread serverThread;
    private Thread clipboardThread;
    private LocalServerSocket serverSocket;

    private String lastClip = "";
    private boolean ignoreNextClip;
    private final Object sendLock = new Object();

    // 通知节流：同一个包 2 秒内只推一条，避免通知风暴把 PC 端刷屏
    private final HashMap<String, Long> lastNotifyAt = new HashMap<>();

    @Override
    public void onCreate() {
        super.onCreate();
        running = true;
        startServer();
        startClipboardWatcher();
        Log.i(TAG, "service created");
    }

    @Override
    public void onDestroy() {
        running = false;
        try { if (serverSocket != null) serverSocket.close(); } catch (Exception ignored) { }
        writer = null;
        super.onDestroy();
        Log.i(TAG, "service destroyed");
    }

    // ---------------------------------------------------------------- 通道

    private void startServer() {
        serverThread = new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    serverSocket = new LocalServerSocket(SOCKET_NAME);
                    while (running) {
                        final LocalSocket socket = serverSocket.accept();
                        new Thread(new Runnable() {
                            @Override
                            public void run() {
                                serve(socket);
                            }
                        }, "ljc-client").start();
                    }
                } catch (Exception e) {
                    Log.e(TAG, "server loop ended", e);
                }
            }
        }, "ljc-server");
        serverThread.setDaemon(true);
        serverThread.start();
    }

    private void serve(LocalSocket socket) {
        PrintWriter out = null;
        try {
            BufferedReader in = new BufferedReader(
                    new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8));
            OutputStream os = socket.getOutputStream();
            out = new PrintWriter(new OutputStreamWriter(os, StandardCharsets.UTF_8), false);
            synchronized (sendLock) {
                writer = out;
            }
            String line;
            while (running && (line = in.readLine()) != null) {
                if (line.trim().isEmpty()) continue;
                try {
                    handle(line);
                } catch (Exception e) {
                    Log.w(TAG, "bad command: " + line, e);
                }
            }
        } catch (Exception e) {
            Log.w(TAG, "client gone", e);
        } finally {
            synchronized (sendLock) {
                if (writer == out) writer = null;
            }
            try { socket.close(); } catch (Exception ignored) { }
        }
    }

    private void send(JSONObject obj) {
        PrintWriter w = writer;
        if (w == null) return;
        synchronized (sendLock) {
            try {
                w.println(obj.toString());
                w.flush();
            } catch (Exception e) {
                Log.w(TAG, "send failed", e);
            }
        }
    }

    private void send(String type) {
        try {
            send(new JSONObject().put("t", type));
        } catch (Exception ignored) { }
    }

    // ---------------------------------------------------------------- 命令

    private void handle(String line) throws Exception {
        JSONObject cmd = new JSONObject(line);
        String type = cmd.optString("t", "");
        if ("ping".equals(type)) {
            send("pong");
        } else if ("clip".equals(type)) {
            setClipboard(cmd.optString("text", ""));
        } else if ("perm".equals(type)) {
            sendPermissions();
        } else if ("perm_page".equals(type)) {
            openPermissionPage();
        } else if ("sync".equals(type)) {
            String what = cmd.optString("what", "");
            if ("apps".equals(what)) {
                new Thread(new Runnable() {
                    @Override public void run() { syncApps(); }
                }, "ljc-apps").start();
            } else if ("sms".equals(what)) {
                new Thread(new Runnable() {
                    @Override public void run() { syncSms(); }
                }, "ljc-sms").start();
            } else if ("media".equals(what)) {
                new Thread(new Runnable() {
                    @Override public void run() { syncMedia(); }
                }, "ljc-media").start();
            }
        } else if ("media".equals(type)) {
            handleMediaCommand(cmd.optString("cmd", ""));
        } else {
            Log.w(TAG, "unknown command type: " + type);
        }
    }

    private void setClipboard(final String text) {
        if (text == null) return;
        main.post(new Runnable() {
            @Override
            public void run() {
                try {
                    ClipboardManager cm = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
                    if (cm == null) return;
                    ignoreNextClip = true;
                    lastClip = text;
                    cm.setPrimaryClip(ClipData.newPlainText("lengjiao", text));
                } catch (Exception e) {
                    Log.w(TAG, "setClipboard failed", e);
                }
            }
        });
    }

    private void sendPermissions() {
        try {
            boolean sms = checkSelfPermission("android.permission.READ_SMS") == PackageManager.PERMISSION_GRANTED;
            boolean notif = isNotificationListenerEnabled();
            send(new JSONObject().put("t", "perm").put("sms", sms).put("notif", notif));
        } catch (Exception e) {
            Log.w(TAG, "sendPermissions", e);
        }
    }

    private boolean isNotificationListenerEnabled() {
        try {
            String flat = Settings.Secure.getString(getContentResolver(), "enabled_notification_listeners");
            return flat != null && flat.contains(getPackageName());
        } catch (Exception e) {
            return false;
        }
    }

    private void openPermissionPage() {
        try {
            Intent intent = new Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS);
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            startActivity(intent);
        } catch (Exception e) {
            Log.w(TAG, "openPermissionPage", e);
        }
    }

    // ---------------------------------------------------------------- 剪贴板 → PC

    private void startClipboardWatcher() {
        clipboardThread = new Thread(new Runnable() {
            @Override
            public void run() {
                ClipboardManager cm = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
                while (running) {
                    try {
                        Thread.sleep(800);
                        if (cm == null || !cm.hasPrimaryClip()) continue;
                        ClipData clip = cm.getPrimaryClip();
                        if (clip == null || clip.getItemCount() == 0) continue;
                        CharSequence cs = clip.getItemAt(0).coerceToText(HelperService.this);
                        if (cs == null) continue;
                        String text = cs.toString();
                        if (text.length() == 0 || text.equals(lastClip)) continue;
                        lastClip = text;
                        if (ignoreNextClip) {
                            ignoreNextClip = false;
                            continue;
                        }
                        send(new JSONObject().put("t", "clip").put("text", text));
                    } catch (InterruptedException ie) {
                        return;
                    } catch (Exception e) {
                        // Android 10+ 后台读剪贴板可能被拒，属于系统限制，不刷日志
                    }
                }
            }
        }, "ljc-clip");
        clipboardThread.setDaemon(true);
        clipboardThread.start();
    }

    // ---------------------------------------------------------------- 通知

    @Override
    public void onNotificationPosted(StatusBarNotification sbn) {
        try {
            if (sbn == null) return;
            Notification n = sbn.getNotification();
            if (n == null) return;

            String pkg = sbn.getPackageName();
            if (pkg == null) return;
            if (pkg.equals("android") || pkg.equals("com.android.systemui")
                    || pkg.equals("com.android.providers.downloads")) return;

            if (n.priority <= Notification.PRIORITY_MIN) return;
            if ((n.flags & Notification.FLAG_ONGOING_EVENT) != 0) return;
            if ((n.flags & Notification.FLAG_GROUP_SUMMARY) != 0) return;
            if (!sbn.isClearable()) return;

            if (n.category != null) {
                String cat = n.category;
                if (Notification.CATEGORY_SERVICE.equals(cat)
                        || Notification.CATEGORY_PROGRESS.equals(cat)
                        || Notification.CATEGORY_SYSTEM.equals(cat)
                        || Notification.CATEGORY_STATUS.equals(cat)) return;
            }

            long now = System.currentTimeMillis();
            Long prev = lastNotifyAt.get(pkg);
            if (prev != null && now - prev < 2000) return;
            lastNotifyAt.put(pkg, now);

            Bundle extras = n.extras;
            String title = text(extras, Notification.EXTRA_TITLE);
            String body = text(extras, Notification.EXTRA_TEXT);
            if (title.isEmpty() && body.isEmpty()) return;

            send(new JSONObject()
                    .put("t", "notif")
                    .put("app", appLabel(pkg))
                    .put("title", title)
                    .put("text", body));
        } catch (Exception e) {
            // 任何一条通知解析失败都不能让监听服务挂掉
            Log.w(TAG, "onNotificationPosted", e);
        }
    }

    /** EXTRA_TITLE / EXTRA_TEXT 经常是 SpannableString，必须按 CharSequence 取，不能 getString。 */
    private static String text(Bundle extras, String key) {
        if (extras == null) return "";
        Object value = extras.get(key);
        if (value == null) return "";
        if (value instanceof CharSequence) return value.toString();
        return String.valueOf(value);
    }

    private String appLabel(String pkg) {
        try {
            PackageManager pm = getPackageManager();
            ApplicationInfo ai = pm.getApplicationInfo(pkg, 0);
            return pm.getApplicationLabel(ai).toString();
        } catch (Exception e) {
            return pkg;
        }
    }

    // ---------------------------------------------------------------- 短信

    private void syncSms() {
        try {
            Uri uri = Uri.parse("content://sms/inbox");
            String[] projection = { "address", "date", "body" };
            int sent = 0;
            android.database.Cursor c = getContentResolver().query(
                    uri, projection, null, null, "date DESC");
            if (c == null) return;
            try {
                int limit = 50;
                while (c.moveToNext() && limit-- > 0) {
                    String addr = c.getString(0);
                    long date = c.getLong(1);
                    String body = c.getString(2);
                    send(new JSONObject()
                            .put("t", "sms")
                            .put("addr", addr == null ? "" : addr)
                            .put("date", date)
                            .put("body", body == null ? "" : body));
                    sent++;
                }
            } finally {
                c.close();
            }
            Log.i(TAG, "sms sent: " + sent);
        } catch (Exception e) {
            Log.w(TAG, "syncSms", e);
        }
    }

    // ---------------------------------------------------------------- 应用列表

    private void syncApps() {
        try {
            PackageManager pm = getPackageManager();
            List<ApplicationInfo> all = pm.getInstalledApplications(0);
            int count = 0;
            for (ApplicationInfo ai : all) {
                boolean system = (ai.flags & ApplicationInfo.FLAG_SYSTEM) != 0;
                Intent launch = pm.getLaunchIntentForPackage(ai.packageName);
                if (system && launch == null) continue;

                String name = pm.getApplicationLabel(ai).toString();
                String icon = iconBase64(pm, ai);
                send(new JSONObject()
                        .put("t", "app")
                        .put("pkg", ai.packageName)
                        .put("name", name)
                        .put("icon", icon));
                count++;
            }
            send(new JSONObject().put("t", "apps_end").put("count", count));
            Log.i(TAG, "apps sent: " + count);
        } catch (Exception e) {
            Log.w(TAG, "syncApps", e);
        }
    }

    private String iconBase64(PackageManager pm, ApplicationInfo ai) {
        try {
            Drawable drawable = pm.getApplicationIcon(ai);
            Bitmap bitmap;
            if (drawable instanceof BitmapDrawable) {
                bitmap = ((BitmapDrawable) drawable).getBitmap();
            } else {
                int size = 96;
                bitmap = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888);
                Canvas canvas = new Canvas(bitmap);
                drawable.setBounds(0, 0, size, size);
                drawable.draw(canvas);
            }
            Bitmap scaled = Bitmap.createScaledBitmap(bitmap, 48, 48, true);
            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            scaled.compress(Bitmap.CompressFormat.PNG, 90, bos);
            return Base64.encodeToString(bos.toByteArray(), Base64.NO_WRAP);
        } catch (Exception e) {
            return "";
        }
    }

    // ---------------------------------------------------------------- 媒体

    private void syncMedia() {
        try {
            MediaController controller = pickController();
            if (controller == null) {
                send(new JSONObject().put("t", "media").put("title", "").put("artist", "")
                        .put("playing", false).put("pkg", ""));
                return;
            }

            MediaMetadata meta = controller.getMetadata();
            PlaybackState state = controller.getPlaybackState();
            String title = meta == null ? "" : str(meta, MediaMetadata.METADATA_KEY_TITLE);
            String artist = meta == null ? "" : str(meta, MediaMetadata.METADATA_KEY_ARTIST);
            boolean playing = state != null && state.getState() == PlaybackState.STATE_PLAYING;
            String pkg = controller.getPackageName();

            send(new JSONObject()
                    .put("t", "media")
                    .put("title", title)
                    .put("artist", artist)
                    .put("playing", playing)
                    .put("pkg", pkg == null ? "" : pkg));
        } catch (Exception e) {
            Log.w(TAG, "syncMedia", e);
        }
    }

    private static String str(MediaMetadata meta, String key) {
        CharSequence cs = meta.getText(key);
        return cs == null ? "" : cs.toString();
    }

    /** 优先挑正在播放的会话，挑不到再用第一个，避免多会话时抓到后台那个。 */
    private MediaController pickController() {
        try {
            MediaSessionManager msm = (MediaSessionManager) getSystemService(Context.MEDIA_SESSION_SERVICE);
            if (msm == null) return null;
            ComponentName cn = new ComponentName(this, HelperService.class);
            List<MediaController> controllers = msm.getActiveSessions(cn);
            if (controllers == null || controllers.isEmpty()) return null;

            for (MediaController c : controllers) {
                PlaybackState s = c.getPlaybackState();
                if (s != null && s.getState() == PlaybackState.STATE_PLAYING) return c;
            }
            return controllers.get(0);
        } catch (Exception e) {
            Log.w(TAG, "getActiveSessions", e);
            return null;
        }
    }

    private void handleMediaCommand(String cmd) {
        try {
            MediaController c = pickController();
            if (c == null) return;
            MediaController.TransportControls tc = c.getTransportControls();
            String action = cmd == null ? "" : cmd.toLowerCase(Locale.ROOT);
            if ("play".equals(action)) tc.play();
            else if ("pause".equals(action)) tc.pause();
            else if ("next".equals(action)) tc.skipToNext();
            else if ("prev".equals(action)) tc.skipToPrevious();
            else return;

            main.postDelayed(new Runnable() {
                @Override public void run() { syncMedia(); }
            }, 450);
        } catch (Exception e) {
            Log.w(TAG, "media command", e);
        }
    }
}
