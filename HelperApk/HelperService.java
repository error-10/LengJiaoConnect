package com.lengjiao.helper;

import android.service.notification.NotificationListenerService;
import android.service.notification.StatusBarNotification;
import android.util.Log;
import android.os.Bundle;
import android.app.Notification;
import android.content.ClipboardManager;
import android.content.ClipData;
import android.net.LocalServerSocket;
import android.net.LocalSocket;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import android.os.Handler;
import android.os.Looper;

public class HelperService extends NotificationListenerService {
    private OutputStream outStream;
    private ClipboardManager cb;
    private boolean ignoreNextClip = false;
    private Handler handler;

    @Override
    public void onCreate() {
        super.onCreate();
        handler = new Handler(Looper.getMainLooper());
        cb = (ClipboardManager) getSystemService(CLIPBOARD_SERVICE);

        // 监听剪贴板
        cb.addPrimaryClipChangedListener(() -> {
            if (ignoreNextClip) {
                ignoreNextClip = false;
                return;
            }
            if (cb.hasPrimaryClip()) {
                ClipData cd = cb.getPrimaryClip();
                if (cd != null && cd.getItemCount() > 0) {
                    CharSequence text = cd.getItemAt(0).getText();
                    if (text != null) {
                        sendToPc("CLIP_EVENT:" + text.toString());
                    }
                }
            }
        });

        // 启动 Socket 服务端
        new Thread(() -> {
            try {
                LocalServerSocket server = new LocalServerSocket("lengjiao_helper");
                Log.d("LengJiao", "Socket Server Started on lengjiao_helper");
                while (true) {
                    LocalSocket client = server.accept();
                    Log.d("LengJiao", "PC Connected!");
                    outStream = client.getOutputStream();
                    InputStream in = client.getInputStream();
                    
                    byte[] buffer = new byte[65536];
                    int len;
                    while ((len = in.read(buffer)) != -1) {
                        String msg = new String(buffer, 0, len, StandardCharsets.UTF_8).trim();
                        if (msg.startsWith("SET_CLIP:")) {
                            String clipText = msg.substring(9);
                            handler.post(() -> {
                                ignoreNextClip = true;
                                ClipData clip = ClipData.newPlainText("LengJiao", clipText);
                                cb.setPrimaryClip(clip);
                            });
                        } else if (msg.equals("CMD:CHECK_PERMISSIONS")) {
                            boolean hasSms = checkSelfPermission(android.Manifest.permission.READ_SMS) == android.content.pm.PackageManager.PERMISSION_GRANTED;
                            sendToPc("PERM_STATUS:" + (hasSms ? "OK" : "FAIL"));
                        } else if (msg.equals("CMD:REQUEST_MANUAL_PERMISSIONS")) {
                            android.content.Intent intent = new android.content.Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS);
                            intent.setData(android.net.Uri.parse("package:" + getPackageName()));
                            intent.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK);
                            startActivity(intent);
                        } else if (msg.equals("CMD:SYNC_ALL")) {
                            syncMedia();
                            syncSms();
                            syncApps();
                        } else if (msg.equals("CMD:REQ_APPS")) {
                            syncApps();
                        } else if (msg.equals("CMD:REQ_SMS")) {
                            syncSms();
                        } else if (msg.equals("CMD:REQ_MEDIA")) {
                            syncMedia();
                        } else if (msg.startsWith("CMD:MEDIA_")) {
                            handleMediaCmd(msg.substring(4));
                        }
                    }
                }
            } catch (Exception e) {
                e.printStackTrace();
            }
        }).start();
    }

    @Override
    public void onListenerConnected() {
        Log.d("LengJiao", "NotificationListener connected!");
    }

    // 记录包名和最后发送时间，用于限流
    private java.util.HashMap<String, Long> lastNotifTime = new java.util.HashMap<>();

    @Override
    public void onNotificationPosted(StatusBarNotification sbn) {
        if (sbn == null) return;
        Notification notif = sbn.getNotification();
        if (notif == null) return;

        // 获取通知的真实重要性（过滤静音通知）
        NotificationListenerService.Ranking ranking = new NotificationListenerService.Ranking();
        if (getCurrentRanking().getRanking(sbn.getKey(), ranking)) {
            if (ranking.getImportance() <= android.app.NotificationManager.IMPORTANCE_LOW) {
                // 如果是静音通知（LOW 或 MIN），直接丢弃
                return;
            }
        }

        // 【终极杀手锏】：如果通知无法被用户滑动清除（常驻通知），直接丢弃！
        if (!sbn.isClearable()) return;

        // 过滤掉常驻通知、前台服务通知、本地进度条通知（防止通知风暴）
        if ((notif.flags & Notification.FLAG_ONGOING_EVENT) != 0) return;
        if ((notif.flags & Notification.FLAG_FOREGROUND_SERVICE) != 0) return;
        if ((notif.flags & Notification.FLAG_LOCAL_ONLY) != 0) return;
        if ((notif.flags & Notification.FLAG_GROUP_SUMMARY) != 0) return;

        // 过滤特定 Category 的系统通知
        if (Notification.CATEGORY_SERVICE.equals(notif.category) ||
            Notification.CATEGORY_PROGRESS.equals(notif.category) ||
            Notification.CATEGORY_SYSTEM.equals(notif.category) ||
            Notification.CATEGORY_STATUS.equals(notif.category)) {
            return;
        }

        Bundle extras = notif.extras;
        String title = extras.getString(Notification.EXTRA_TITLE, "");
        CharSequence textSeq = extras.getCharSequence(Notification.EXTRA_TEXT);
        String text = textSeq != null ? textSeq.toString() : "";
        String pkg = sbn.getPackageName();

        // 进一步过滤常见的系统无用通知
        if (pkg.equals("android") || pkg.equals("com.android.systemui") || pkg.equals("com.android.providers.downloads")) return;

        // 获取人类可读的应用名称 (如 "微信" 而不是 "com.tencent.mm")
        android.content.pm.PackageManager pm = getPackageManager();
        String appName = pkg;
        try {
            android.content.pm.ApplicationInfo ai = pm.getApplicationInfo(pkg, 0);
            CharSequence label = pm.getApplicationLabel(ai);
            if (label != null) appName = label.toString();
        } catch (Exception e) {}

        // 如果标题和内容都为空，忽略
        if (title.isEmpty() && text.isEmpty()) return;

        // 【频率限制】同一个包名的通知，3秒内最多允许推1条
        long now = System.currentTimeMillis();
        Long lastTime = lastNotifTime.get(pkg);
        if (lastTime != null && now - lastTime < 3000) {
            return;
        }
        lastNotifTime.put(pkg, now);

        Log.d("LengJiao", "NOTIF|" + appName + "|" + title + "|" + text);
        sendToPc("NOTIF_EVENT:" + appName + "|" + title + "|" + text);
    }

    @Override
    public void onNotificationRemoved(StatusBarNotification sbn) {
    }

    private void sendToPc(String message) {
        if (outStream != null) {
            try {
                outStream.write((message + "\0").getBytes(StandardCharsets.UTF_8));
                outStream.flush();
            } catch (Exception e) {
            }
        }
    }

    private void syncSms() {
        new Thread(() -> {
            try {
                android.database.Cursor cursor = getContentResolver().query(android.net.Uri.parse("content://sms/inbox"), null, null, null, "date DESC LIMIT 50");
                if (cursor != null) {
                    while (cursor.moveToNext()) {
                        String address = cursor.getString(cursor.getColumnIndexOrThrow("address"));
                        String body = cursor.getString(cursor.getColumnIndexOrThrow("body"));
                        long date = cursor.getLong(cursor.getColumnIndexOrThrow("date"));
                        sendToPc("SMS_DATA:" + address + "|" + date + "|" + body.replace("\n", " ").replace("|", " "));
                    }
                    cursor.close();
                }
            } catch (Exception e) { e.printStackTrace(); }
        }).start();
    }

    private void syncApps() {
        new Thread(() -> {
            try {
                android.content.pm.PackageManager pm = getPackageManager();
                java.util.List<android.content.pm.ApplicationInfo> packages = pm.getInstalledApplications(android.content.pm.PackageManager.GET_META_DATA);
                for (android.content.pm.ApplicationInfo packageInfo : packages) {
                    if (pm.getLaunchIntentForPackage(packageInfo.packageName) != null) {
                        String appName = pm.getApplicationLabel(packageInfo).toString();
                        android.graphics.drawable.Drawable icon = pm.getApplicationIcon(packageInfo);
                        
                        android.graphics.Bitmap bitmap = null;
                        if (icon instanceof android.graphics.drawable.BitmapDrawable) {
                            bitmap = ((android.graphics.drawable.BitmapDrawable) icon).getBitmap();
                        } else {
                            bitmap = android.graphics.Bitmap.createBitmap(Math.max(icon.getIntrinsicWidth(), 1), Math.max(icon.getIntrinsicHeight(), 1), android.graphics.Bitmap.Config.ARGB_8888);
                            android.graphics.Canvas canvas = new android.graphics.Canvas(bitmap);
                            icon.setBounds(0, 0, canvas.getWidth(), canvas.getHeight());
                            icon.draw(canvas);
                        }
                        
                        android.graphics.Bitmap resized = android.graphics.Bitmap.createScaledBitmap(bitmap, 48, 48, true);
                        java.io.ByteArrayOutputStream baos = new java.io.ByteArrayOutputStream();
                        resized.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, baos);
                        byte[] b = baos.toByteArray();
                        String base64Icon = android.util.Base64.encodeToString(b, android.util.Base64.NO_WRAP);
                        
                        sendToPc("APP_DATA:" + packageInfo.packageName + "|" + appName + "|" + base64Icon);
                    }
                }
            } catch (Exception e) { e.printStackTrace(); }
        }).start();
    }

    private void syncMedia() {
        try {
            android.media.session.MediaSessionManager msm = (android.media.session.MediaSessionManager) getSystemService(android.content.Context.MEDIA_SESSION_SERVICE);
            java.util.List<android.media.session.MediaController> controllers = msm.getActiveSessions(new android.content.ComponentName(this, HelperService.class));
            if (controllers != null && controllers.size() > 0) {
                android.media.session.MediaController mc = controllers.get(0);
                android.media.MediaMetadata metadata = mc.getMetadata();
                String title = metadata != null ? metadata.getString(android.media.MediaMetadata.METADATA_KEY_TITLE) : "未知";
                String artist = metadata != null ? metadata.getString(android.media.MediaMetadata.METADATA_KEY_ARTIST) : "未知";
                if (title == null) title = "未知";
                if (artist == null) artist = "未知";
                
                android.media.session.PlaybackState pbState = mc.getPlaybackState();
                boolean isPlaying = pbState != null && pbState.getState() == android.media.session.PlaybackState.STATE_PLAYING;
                
                sendToPc("MEDIA_DATA:" + title.replace("|", " ") + "|" + artist.replace("|", " ") + "|" + (isPlaying ? "1" : "0"));
            } else {
                sendToPc("MEDIA_DATA:暂无播放|暂无|0");
            }
        } catch (Exception e) { e.printStackTrace(); }
    }



    private void handleMediaCmd(String cmd) {
        try {
            android.media.session.MediaSessionManager msm = (android.media.session.MediaSessionManager) getSystemService(android.content.Context.MEDIA_SESSION_SERVICE);
            java.util.List<android.media.session.MediaController> controllers = msm.getActiveSessions(new android.content.ComponentName(this, HelperService.class));
            if (controllers != null && controllers.size() > 0) {
                android.media.session.MediaController mc = controllers.get(0);
                android.media.session.MediaController.TransportControls tc = mc.getTransportControls();
                if ("MEDIA_PLAY".equals(cmd)) tc.play();
                else if ("MEDIA_PAUSE".equals(cmd)) tc.pause();
                else if ("MEDIA_NEXT".equals(cmd)) tc.skipToNext();
                else if ("MEDIA_PREV".equals(cmd)) tc.skipToPrevious();
                
                handler.postDelayed(() -> syncMedia(), 500);
            }
        } catch (Exception e) { e.printStackTrace(); }
    }
}
