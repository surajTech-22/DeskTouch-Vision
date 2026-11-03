package com.desktouch.vision;

import android.app.*;
import android.content.*;
import android.graphics.*;
import android.os.*;
import android.provider.Settings;
import android.view.*;
import android.widget.*;
import java.util.List;

/** DeskTouch Vision Android: hardware keyboard/mouse settings belong to Android itself. */
public final class MainActivity extends Activity {
    private TextView status,devices;
    private LinearLayout deviceList;
    private TextView connectionSummary;
    private DeskTouchSession session;
    private View pointerPad;
    private float prevX,prevY,downX,downY;
    private boolean dragged;
    private volatile boolean scanning;

    @Override public void onCreate(Bundle savedState){
        super.onCreate(savedState);
        session=DeskTouchRuntime.session(this);
        DeskTouchRuntime.setListener(message ->
            runOnUiThread(()->{if(status!=null)status.setText(message);}));
        // Android 13+ requires permission to show the persistent connection
        // notification in the shade. The foreground service itself is still
        // subject to Android's background lifecycle rules.
        if(Build.VERSION.SDK_INT>=33 &&
            checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)
                !=android.content.pm.PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{android.Manifest.permission.POST_NOTIFICATIONS},37377);
        }
        if(AutoConnectService.enabled(this)) {
            try {AutoConnectService.start(this);}
            catch(RuntimeException startupError) {
                android.util.Log.w("DeskTouch","Background startup not available",startupError);
            }
        }
        LinearLayout page=new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(16),dp(16),dp(16),dp(12));
        page.setBackgroundColor(Color.rgb(30,32,37));
        ScrollView scroll=new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.addView(page);
        setContentView(scroll);
        TextView heading=new TextView(this);
        heading.setText("DeskTouch Vision");
        heading.setTextSize(27);
        heading.setTextColor(Color.WHITE);
        page.addView(heading);
        TextView hint=new TextView(this);
        hint.setText("One-way mode: control this Android screen from connected Windows PCs");
        hint.setTextColor(Color.LTGRAY);
        hint.setPadding(0,dp(4),0,dp(12));
        page.addView(hint);
        status=new TextView(this);
        status.setText("Scan for your Windows computer on the same private Wi-Fi.");
        status.setTextColor(Color.rgb(157,203,245));
        status.setTextSize(15);
        status.setPadding(0,dp(6),0,dp(14));
        page.addView(status);
        addButton(page,"Find devices",this::scan);
        connectionSummary=new TextView(this);
        connectionSummary.setTextColor(Color.rgb(171,216,188));
        connectionSummary.setPadding(0,dp(5),0,dp(9));
        connectionSummary.setText("Connected devices: "+DeskTouchRuntime.connectedCount()+" / 8");
        page.addView(connectionSummary);
        deviceList=new LinearLayout(this);
        deviceList.setOrientation(LinearLayout.VERTICAL);
        page.addView(deviceList);
        android.widget.CheckBox allowWindows=new android.widget.CheckBox(this);
        allowWindows.setText("Allow approved Windows PCs to control this Android screen");
        allowWindows.setTextColor(Color.WHITE);
        allowWindows.setChecked(DeskTouchAccessibilityService.allowed(this));
        allowWindows.setOnCheckedChangeListener((v,enabled)->{
            DeskTouchAccessibilityService.setAllowed(this,enabled);
            if(enabled && !DeskTouchAccessibilityService.ready(this)){
                status.setText("Enable 'DeskTouch Windows control' in Android Accessibility settings, then reconnect.");
                startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS));
            }else status.setText(enabled
                ? "Windows control enabled. Reconnect this phone so Windows detects the new capability."
                : "Windows control disabled. Android touch has priority.");
        });
        page.addView(allowWindows);
        addButton(page,"Open Android Accessibility settings",()->
            startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)));
        addButton(page,"Stop Windows control on this phone",()->{
            DeskTouchRuntime.remoteNativeTouch();
            status.setText("Windows input authority returned to Android.");
        });
        addButton(page,"Trust connected PC (auto reconnect identity)",()->session.requestTrust());
        addButton(page,"Disconnect selected device",()->{
            AutoConnectService.pausePeer(this,DeskTouchRuntime.selectedIdentity());
            session.releaseControl();
            session.disconnect();
            status.setText("Selected device disconnected. Other peers stay connected; "+
                "auto reconnect paused only for this device.");
        });
        android.widget.CheckBox autoStart=new android.widget.CheckBox(this);
        autoStart.setText("Launch on Android startup and reconnect trusted devices");
        autoStart.setTextColor(Color.WHITE);
        autoStart.setChecked(AutoConnectService.enabled(this));
        autoStart.setOnCheckedChangeListener((button,checked)->{
            AutoConnectService.setEnabled(this,checked);
            AutoConnectService.setReconnect(this,checked);
            status.setText(checked
                ? "Android startup enabled; trusted PCs reconnect automatically when reachable."
                : "Android startup and background reconnect disabled.");
        });
        page.addView(autoStart);
        addButton(page,"Get updates",()->AndroidUpdater.checkForUpdates(this,true));
        addButton(page,"Update existing application",()->AndroidUpdater.installAvailableUpdate(this));
        // Only checks official GitHub metadata; never downloads or silently
        // installs an executable without explicit user action.
        AndroidUpdater.checkForUpdates(this,false);
        TextView note=new TextView(this);
        note.setText("1. Enable Windows control and approve the Android Accessibility service. " +
            "2. Reconnect this phone to the Windows DeskTouch app. " +
            "3. In Windows Displays select this Android phone and position its screen. " +
            "4. Move the Windows cursor to that outside edge. Windows mouse taps and " +
            "typing in ordinary editable fields then control Android. " +
            "Android touch always cancels remote control. Password fields remain protected. " +
            "This is not an Android virtual display or full hardware keyboard driver.");
        note.setTextColor(Color.LTGRAY);
        note.setPadding(0,dp(14),0,0);
        page.addView(note);

    }
    private int dp(int size){return Math.round(getResources().getDisplayMetrics().density*size);}
    private void addButton(LinearLayout parent,String label,Runnable action){
        Button b=new Button(this);
        b.setText(label);b.setAllCaps(false);
        b.setOnClickListener(v->action.run());
        parent.addView(b,new LinearLayout.LayoutParams(-1,-2));
    }
    private void scan(){
        if(scanning)return;
        scanning=true;
        status.setText("Finding trusted Windows PCs; Windows will control this phone, not vice versa...");
        deviceList.removeAllViews();
        new Thread(()->{
            try{
                List<DesktopDiscovery.Device> list=DesktopDiscovery.scan();
                runOnUiThread(()->{
                    connectionSummary.setText("Connected devices: "+
                        DeskTouchRuntime.connectedCount()+" / 8");
                    if(list.isEmpty())status.setText("No devices found. Check trusted Wi-Fi and DeskTouch tray/background service.");
                    else status.setText("DeskTouch found "+list.size()+" nearby device(s). "+
                        "Interconnected PCs are grouped; choose a member to connect.");
                    // One visible connection card per linked Windows network.
                    // Direct TLS identities remain separate underneath; a group
                    // must never be substituted for a certificate fingerprint.
                    java.util.LinkedHashMap<String,List<DesktopDiscovery.Device>> groups=
                        DesktopDiscovery.windowsGroups(list);
                    java.util.Set<String> connectedFingerprints=new java.util.HashSet<>();
                    for(DesktopDiscovery.Device device:list)
                        if(!device.android&&DeskTouchRuntime.isConnected(device.fingerprint))
                            connectedFingerprints.add(device.fingerprint);
                    java.util.Set<String> approved=new java.util.HashSet<>();
                    try {approved.addAll(new TrustedWindows(this).fingerprints());}
                    catch(Exception ignored){}
                    long activeGroups=groups.values().stream().filter(members->
                        members.stream().anyMatch(m->connectedFingerprints.contains(m.fingerprint)))
                        .count();
                    connectionSummary.setText("Connected Windows networks: "+
                        activeGroups+" / "+groups.size());
                    for(List<DesktopDiscovery.Device> members:groups.values()){
                        DesktopDiscovery.Device gateway=DesktopDiscovery.chooseGateway(
                            members,connectedFingerprints,approved);
                        if(gateway==null)continue;
                        boolean connected=members.stream().anyMatch(
                            m->connectedFingerprints.contains(m.fingerprint));
                        String names=String.join(" + ",members.stream()
                            .map(m->m.name).toArray(String[]::new));
                        String label=(members.size()>1?"Linked Windows network":"Windows PC")
                            +" • "+names
                            +(connected?" • Connected via "+gateway.name:
                              gateway.ready?" • Connect":" • At capacity");
                        addButton(deviceList,label,()->{
                            if(DeskTouchRuntime.isConnected(gateway.fingerprint)){
                                session=DeskTouchRuntime.select(this,gateway.fingerprint);
                                status.setText("Connected to "+names+" through "+gateway.name+
                                    ". Network members keep their own TLS identities. "+
                                    "This connection does not automatically relay Android "+
                                    "keyboard input through another PC.");
                                return;
                            }
                            if(!gateway.ready){
                                status.setText("No available Windows gateway. Rescan after the "+
                                    "connected PCs start DeskTouch or release a session.");
                                return;
                            }
                            new AlertDialog.Builder(this).setTitle(
                                members.size()>1?"Connect to linked Windows network":"Secure pairing")
                                .setMessage("DeskTouch will pair with "+gateway.name+
                                    " as this network's gateway. Approve the pairing on "+
                                    "that Windows PC. Other linked PCs remain separately "+
                                    "authenticated; selecting a group does not install "+
                                    "a transparent keyboard relay.")
                                .setNegativeButton("Cancel",null)
                                .setPositiveButton("Connect",(dialog,which)->{
                                    android.graphics.Point size=new android.graphics.Point();
                                    getWindowManager().getDefaultDisplay().getRealSize(size);
                                    AutoConnectService.setReconnect(this,true);
                                    AutoConnectService.resumePeer(this,gateway.fingerprint);
                                    session=DeskTouchRuntime.select(this,gateway.fingerprint);
                                    session.connect(gateway,size.x,size.y);
                                    status.setText("Connecting to "+names+" via "+
                                        gateway.name+"; authorize on that Windows PC.");
                                }).show();
                        });
                    }
                    // Android peers remain individually identifiable: only
                    // interconnected Windows PCs share a network grouping.
                    for(DesktopDiscovery.Device d:list)
                        if(d.android)
                            addButton(deviceList,"Android • "+d.name+
                                " • discoverable only",()->status.setText(
                                "Select Windows control from that Android device."));
                });
            }catch(Exception e){runOnUiThread(()->status.setText("LAN scan failed: "+e.getMessage()));}
            finally{scanning=false;}
        },"DeskTouch LAN discovery").start();
    }
    @Override public void onWindowFocusChanged(boolean focus){
        super.onWindowFocusChanged(focus);
        // Do not revoke the remote input lease simply because the Android
        // user opened an app to be controlled. Physical touch revokes it.
    }
    @Override protected void onDestroy(){
        DeskTouchRuntime.setListener(null);
        // The foreground reconnect service owns the authenticated TLS sessions.
        // Closing this setup screen must not disconnect the selected phone.
        super.onDestroy();
    }
}
