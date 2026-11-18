package com.desktouch.vision;

import android.app.*;
import android.content.*;
import android.content.res.ColorStateList;
import android.graphics.*;
import android.graphics.drawable.GradientDrawable;
import android.os.*;
import android.provider.Settings;
import android.text.*;
import android.view.*;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;
import android.view.inputmethod.InputConnectionWrapper;
import android.view.inputmethod.InputMethodManager;
import android.widget.*;
import androidx.core.graphics.Insets;
import androidx.core.view.WindowCompat;
import androidx.core.view.WindowInsetsCompat;
import androidx.core.view.WindowInsetsControllerCompat;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.function.Consumer;

/** DeskTouch Vision Android: hardware keyboard/mouse settings belong to Android itself. */
public final class MainActivity extends Activity {
    private static final String PREFS="desktouch_prefs";
    private static final String PREF_POINTER_PROGRESS="pointer_sensitivity_progress";
    private static final int TAB_COUNT=4;

    private PhoneUiKit.Palette palette;
    private final List<Consumer<PhoneUiKit.Palette>> themeUpdaters=new ArrayList<>();
    private final List<Runnable> dynamicDeviceThemeUpdaters=new ArrayList<>();

    private LinearLayout rootLayout,appHeader,bottomNav;
    private FrameLayout tabFrame;
    private View headerDivider,navDivider;
    private TextView headerTitle,headerVersion,headerSub;
    private TextView status,connectionSummary,liveLinkView,keyboardBindingsView;
    private TextView permissionState,trustSummary,protectionDesc,safetyBadge;
    private LinearLayout liveBadge,deviceList,environmentList;
    private View liveDot;
    private TextView liveBadgeText,mouseTag,mouseMetric,kbTag,kbMetric;
    private Button scanBtn,takeControlBtn,returnControlBtn,trustBadgeBtn,openPadBtn;
    private Button pauseBankingButton,captureMouseBtn,resetPathsBtn,shortcutsBtn;
    private TextView trackpadTargetTitle,trackpadReadout,sensReadout;
    private TextView speedWifiRtt,speedWifiSub,wifiLossBadge,speedMobRtt,speedMobSub,mobLossBadge;
    private SeekBar pointerSensitivity;
    private EditText keyboardInput;
    private Toast activeToast;

    private final ScrollView[] tabScrolls=new ScrollView[TAB_COUNT];
    private final LinearLayout[] tabButtons=new LinearLayout[TAB_COUNT];
    private final PhoneUiKit.TabIconView[] tabIcons=new PhoneUiKit.TabIconView[TAB_COUNT];
    private final TextView[] tabLabels=new TextView[TAB_COUNT];
    private int activeTab;

    private PhoneUiKit.SegmentedControl mouseModeSeg,keyboardModeSeg,pointerFeelSeg,themeSeg;
    private PhoneUiKit.SwitchRow dualPathControlsSwitch,keepChannelsWarmSwitch;
    private PhoneUiKit.SwitchRow autoStartSwitch,allowWindowsSwitch,allowShellBridgeSwitch;
    private PhoneUiKit.SwitchRow autoPausePaymentsSwitch,autoPauseDisconnectSwitch;

    private final Handler liveUiHandler=new Handler(Looper.getMainLooper());
    private boolean liveUiRunning;
    private android.hardware.input.InputManager inputManager;
    private android.hardware.input.InputManager.InputDeviceListener inputDeviceListener;
    private final Runnable liveUiTick=new Runnable(){
        @Override public void run(){
            if(!liveUiRunning)return;
            refreshLiveLinkAndKeyboard();
            liveUiHandler.postDelayed(this,750L);
        }
    };

    private DeskTouchSession session;
    private View pointerPad;
    private float prevX,prevY,downX,downY,twoFingerPrevY,twoFingerScrollCarry;
    private float touchGlowX,touchGlowY;
    private boolean touchGlowActive;
    private boolean dragged,twoFingerMoved;
    private int maxPointerCount;
    private volatile boolean scanning;
    private float pointerSpeed=2.8f;
    private boolean suppressTypedText;

    @Override public void onCreate(Bundle savedState){
        super.onCreate(savedState);
        palette=PhoneUiKit.palette(PhoneUiKit.readTheme(this));
        int savedProgress=getSharedPreferences(PREFS,MODE_PRIVATE)
            .getInt(PREF_POINTER_PROGRESS,180);
        savedProgress=Math.max(0,Math.min(400,savedProgress));
        pointerSpeed=1f+savedProgress/100f;
        session=DeskTouchRuntime.session(this);
        DeskTouchRuntime.setListener(message ->
            runOnUiThread(()->{
                if(isFinishing()||isDestroyed())return;
                if(status!=null)status.setText(message);
                if(environmentList!=null)refreshEnvironmentMembers();
                refreshPermissionState();
                refreshTrustSummary();
                refreshLiveLinkAndKeyboard();
                updateVisibleScreenAwake();
            }));
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

        WindowCompat.setDecorFitsSystemWindows(getWindow(),false);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);

        buildRootLayout(savedProgress);
        setContentView(rootLayout);
        applyWindowInsets();
        applyTheme(palette.themeId);
        selectTab(0);

        DeskTouchRuntime.setLiveStatusListener(()->runOnUiThread(()->{
            if(!isFinishing()&&!isDestroyed())refreshLiveLinkAndKeyboard();
        }));
        registerKeyboardDeviceListener();
        refreshEnvironmentMembers();
        refreshPermissionState();
        refreshTrustSummary();
        refreshStreamOptionControls();
        refreshLiveLinkAndKeyboard();

        // Only checks official GitHub metadata; never downloads or silently
        // installs an executable without explicit user action.
        AndroidUpdater.checkForUpdates(this,false);
    }

    private void buildRootLayout(int initialSensitivityProgress){
        rootLayout=new LinearLayout(this);
        rootLayout.setOrientation(LinearLayout.VERTICAL);

        buildHeader();
        rootLayout.addView(appHeader,new LinearLayout.LayoutParams(-1,-2));
        headerDivider=new View(this);
        rootLayout.addView(headerDivider,new LinearLayout.LayoutParams(-1,Math.max(1,dp(1))));

        tabFrame=new FrameLayout(this);
        LinearLayout.LayoutParams frameParams=new LinearLayout.LayoutParams(-1,0,1f);
        rootLayout.addView(tabFrame,frameParams);

        LinearLayout homePage=createTabPage(0);
        LinearLayout trackpadPage=createTabPage(1);
        LinearLayout speedPage=createTabPage(2);
        LinearLayout setupPage=createTabPage(3);

        buildHomeTab(homePage);
        buildTrackpadTab(trackpadPage,initialSensitivityProgress);
        buildSpeedTab(speedPage);
        buildSetupTab(setupPage);

        navDivider=new View(this);
        rootLayout.addView(navDivider,new LinearLayout.LayoutParams(-1,Math.max(1,dp(1))));
        buildBottomNav();
        rootLayout.addView(bottomNav,new LinearLayout.LayoutParams(-1,-2));
    }

    private LinearLayout createTabPage(int index){
        ScrollView scroll=new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setVerticalScrollBarEnabled(false);
        LinearLayout page=new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(14),dp(12),dp(14),dp(18));
        scroll.addView(page,new FrameLayout.LayoutParams(-1,-2));
        tabScrolls[index]=scroll;
        tabFrame.addView(scroll,new FrameLayout.LayoutParams(-1,-1));
        return page;
    }

    private void buildHeader(){
        appHeader=new LinearLayout(this);
        appHeader.setOrientation(LinearLayout.HORIZONTAL);
        appHeader.setGravity(Gravity.CENTER_VERTICAL);
        appHeader.setPadding(dp(16),dp(10),dp(16),dp(10));

        TextView logo=new TextView(this);
        logo.setText("DT");
        logo.setTextColor(Color.WHITE);
        logo.setTextSize(13f);
        logo.setTypeface(null,Typeface.BOLD);
        logo.setGravity(Gravity.CENTER);
        GradientDrawable logoBg=new GradientDrawable(
            GradientDrawable.Orientation.TL_BR,new int[]{0xFF0EA5E9,0xFF4F46E5});
        logoBg.setCornerRadius(dp(11));
        logo.setBackground(logoBg);
        LinearLayout.LayoutParams logoParams=new LinearLayout.LayoutParams(dp(36),dp(36));
        logoParams.rightMargin=dp(10);
        appHeader.addView(logo,logoParams);

        LinearLayout titleCol=new LinearLayout(this);
        titleCol.setOrientation(LinearLayout.VERTICAL);

        LinearLayout titleRow=new LinearLayout(this);
        titleRow.setOrientation(LinearLayout.HORIZONTAL);
        titleRow.setGravity(Gravity.CENTER_VERTICAL);

        headerTitle=new TextView(this);
        headerTitle.setText("DeskTouch Vision");
        headerTitle.setTextSize(15.5f);
        headerTitle.setTypeface(null,Typeface.BOLD);
        headerTitle.setSingleLine(true);
        headerTitle.setEllipsize(TextUtils.TruncateAt.END);
        titleRow.addView(headerTitle);

        headerVersion=new TextView(this);
        headerVersion.setText("v"+readVersionName());
        headerVersion.setTextSize(10f);
        headerVersion.setTypeface(null,Typeface.BOLD);
        headerVersion.setPadding(dp(6),dp(2),dp(6),dp(2));
        LinearLayout.LayoutParams verLp=new LinearLayout.LayoutParams(-2,-2);
        verLp.leftMargin=dp(6);
        titleRow.addView(headerVersion,verLp);

        titleCol.addView(titleRow);

        headerSub=new TextView(this);
        headerSub.setText("Not connected • Ready to scan");
        headerSub.setTextSize(11.5f);
        headerSub.setSingleLine(true);
        headerSub.setEllipsize(TextUtils.TruncateAt.END);
        titleCol.addView(headerSub);

        LinearLayout.LayoutParams colParams=new LinearLayout.LayoutParams(0,-2,1f);
        colParams.rightMargin=dp(8);
        appHeader.addView(titleCol,colParams);

        pauseBankingButton=PhoneUiKit.createButton(this,"Pay Mode",palette,PhoneUiKit.BTN_TINT_EMERALD);
        pauseBankingButton.setTextSize(11.5f);
        pauseBankingButton.setPadding(dp(11),dp(7),dp(11),dp(7));
        pauseBankingButton.setOnClickListener(v->{
            if(!PermissionContinuity.systemAccessibilityEnabled(this)){
                DeskTouchAccessibilityService.setAllowed(this,true);
                DeskTouchRuntime.accessibilityPermissionChanged(this);
                refreshPermissionState();
                DeskTouchAccessibilityService.openAccessibilitySettings(this,true);
                setFeedback("Turn DeskTouch Windows control back on in Accessibility settings.",true);
            }else{
                boolean selfDisabled=DeskTouchAccessibilityService.pauseForBanking(
                    this,"Turned off from app");
                refreshPermissionState();
                updateVisibleScreenAwake();
                if(selfDisabled){
                    setFeedback("Android Accessibility turned off. Payment apps work normally; connections stay ready.",true);
                }else{
                    DeskTouchAccessibilityService.openAccessibilitySettings(this,false);
                    setFeedback("Tap DeskTouch Windows control in Accessibility settings to turn it off.",true);
                }
            }
        });
        appHeader.addView(pauseBankingButton,new LinearLayout.LayoutParams(-2,-2));

        onTheme(p->{
            appHeader.setBackgroundColor(p.chromeBg);
            headerTitle.setTextColor(p.textPrimary);
            headerSub.setTextColor(p.textSecondary);
            headerVersion.setBackground(PhoneUiKit.roundedBox(
                p.accentSoftBg,Color.TRANSPARENT,dp(6),0));
            headerVersion.setTextColor(p.accentText);
        });
    }

    private void buildHomeTab(LinearLayout page){
        LinearLayout hero=PhoneUiKit.createCard(this,palette,14);
        page.addView(hero,cardParams(0));

        LinearLayout heroTop=new LinearLayout(this);
        heroTop.setOrientation(LinearLayout.HORIZONTAL);
        heroTop.setGravity(Gravity.CENTER_VERTICAL);

        liveBadge=new LinearLayout(this);
        liveBadge.setOrientation(LinearLayout.HORIZONTAL);
        liveBadge.setGravity(Gravity.CENTER_VERTICAL);
        liveBadge.setPadding(dp(10),dp(5),dp(10),dp(5));

        liveDot=new View(this);
        LinearLayout.LayoutParams dotLp=new LinearLayout.LayoutParams(dp(7),dp(7));
        dotLp.rightMargin=dp(6);
        liveBadge.addView(liveDot,dotLp);

        liveBadgeText=new TextView(this);
        liveBadgeText.setText("Offline");
        liveBadgeText.setTextSize(11.5f);
        liveBadgeText.setTypeface(null,Typeface.BOLD);
        liveBadge.addView(liveBadgeText);

        LinearLayout badgeWrap=new LinearLayout(this);
        badgeWrap.setOrientation(LinearLayout.HORIZONTAL);
        badgeWrap.addView(liveBadge);
        heroTop.addView(badgeWrap,new LinearLayout.LayoutParams(0,-2,1f));

        scanBtn=PhoneUiKit.createButton(this,"Find Devices",palette,PhoneUiKit.BTN_SUBTLE);
        scanBtn.setTextSize(12f);
        scanBtn.setPadding(dp(12),dp(7),dp(12),dp(7));
        scanBtn.setOnClickListener(v->scan());
        heroTop.addView(scanBtn,new LinearLayout.LayoutParams(-2,-2));
        hero.addView(heroTop);

        status=new TextView(this);
        status.setText("Scan for your Windows computer on the same private Wi-Fi.");
        status.setTextSize(14f);
        status.setTypeface(null,Typeface.BOLD);
        status.setPadding(0,dp(10),0,0);
        hero.addView(status);

        liveLinkView=new TextView(this);
        liveLinkView.setText("Live link: waiting for connection");
        liveLinkView.setTextSize(12f);
        liveLinkView.setPadding(0,dp(4),0,0);
        liveLinkView.setLineSpacing(dp(2),1f);
        hero.addView(liveLinkView);

        LinearLayout splitRow=new LinearLayout(this);
        splitRow.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams splitLp=new LinearLayout.LayoutParams(-1,-2);
        splitLp.topMargin=dp(10);
        hero.addView(splitRow,splitLp);

        LinearLayout mouseBox=PhoneUiKit.createSubcard(this,palette,10);
        LinearLayout.LayoutParams leftSub=new LinearLayout.LayoutParams(0,-2,1f);
        leftSub.rightMargin=dp(4);
        splitRow.addView(mouseBox,leftSub);

        LinearLayout mTop=new LinearLayout(this);
        mTop.setOrientation(LinearLayout.HORIZONTAL);
        TextView mLbl=new TextView(this);
        mLbl.setText("Mouse Path");
        mLbl.setTextSize(11f);
        mTop.addView(mLbl,new LinearLayout.LayoutParams(0,-2,1f));
        mouseTag=new TextView(this);
        mouseTag.setText("Offline");
        mouseTag.setTextSize(11f);
        mouseTag.setTypeface(null,Typeface.BOLD);
        mTop.addView(mouseTag);
        mouseBox.addView(mTop);

        mouseMetric=new TextView(this);
        mouseMetric.setText("Tap Find Devices");
        mouseMetric.setTextSize(12.5f);
        mouseMetric.setTypeface(null,Typeface.BOLD);
        mouseMetric.setPadding(0,dp(4),0,0);
        mouseMetric.setSingleLine(true);
        mouseMetric.setEllipsize(TextUtils.TruncateAt.END);
        mouseBox.addView(mouseMetric);

        LinearLayout kbBox=PhoneUiKit.createSubcard(this,palette,10);
        LinearLayout.LayoutParams rightSub=new LinearLayout.LayoutParams(0,-2,1f);
        rightSub.leftMargin=dp(4);
        splitRow.addView(kbBox,rightSub);

        LinearLayout kTop=new LinearLayout(this);
        kTop.setOrientation(LinearLayout.HORIZONTAL);
        TextView kLbl=new TextView(this);
        kLbl.setText("Keys Path");
        kLbl.setTextSize(11f);
        kTop.addView(kLbl,new LinearLayout.LayoutParams(0,-2,1f));
        kbTag=new TextView(this);
        kbTag.setText("Offline");
        kbTag.setTextSize(11f);
        kbTag.setTypeface(null,Typeface.BOLD);
        kTop.addView(kbTag);
        kbBox.addView(kTop);

        kbMetric=new TextView(this);
        kbMetric.setText("Auto-reconnect ready");
        kbMetric.setTextSize(12.5f);
        kbMetric.setTypeface(null,Typeface.BOLD);
        kbMetric.setPadding(0,dp(4),0,0);
        kbMetric.setSingleLine(true);
        kbMetric.setEllipsize(TextUtils.TruncateAt.END);
        kbBox.addView(kbMetric);

        deviceList=new LinearLayout(this);
        deviceList.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams devLp=new LinearLayout.LayoutParams(-1,-2);
        devLp.topMargin=dp(6);
        hero.addView(deviceList,devLp);

        LinearLayout heroActions=new LinearLayout(this);
        heroActions.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams actLp=new LinearLayout.LayoutParams(-1,-2);
        actLp.topMargin=dp(10);
        hero.addView(heroActions,actLp);

        takeControlBtn=PhoneUiKit.createButton(this,"Control PC Screen",palette,PhoneUiKit.BTN_PRIMARY);
        takeControlBtn.setOnClickListener(v->{
            DeskTouchRuntime.releaseAll();
            session=DeskTouchRuntime.session(this);
            session.claimControl();
            refreshLiveLinkAndKeyboard();
        });
        LinearLayout.LayoutParams b1Lp=new LinearLayout.LayoutParams(0,-2,1f);
        b1Lp.rightMargin=dp(4);
        heroActions.addView(takeControlBtn,b1Lp);

        returnControlBtn=PhoneUiKit.createButton(this,"Return to Phone",palette,PhoneUiKit.BTN_SUBTLE);
        returnControlBtn.setOnClickListener(v->{
            DeskTouchRuntime.releaseAll();
            setFeedback("Remote Windows input returned. Secure connections remain open.",false);
            refreshLiveLinkAndKeyboard();
        });
        LinearLayout.LayoutParams b2Lp=new LinearLayout.LayoutParams(0,-2,1f);
        b2Lp.leftMargin=dp(4);
        heroActions.addView(returnControlBtn,b2Lp);

        LinearLayout envCard=PhoneUiKit.createCard(this,palette,14);
        page.addView(envCard,cardParams(10));

        LinearLayout envTop=new LinearLayout(this);
        envTop.setOrientation(LinearLayout.HORIZONTAL);
        envTop.setGravity(Gravity.CENTER_VERTICAL);

        connectionSummary=new TextView(this);
        connectionSummary.setText("Connected devices: "+DeskTouchRuntime.connectedCount()+" / 8");
        connectionSummary.setTextSize(11.5f);
        connectionSummary.setTypeface(null,Typeface.BOLD);
        envTop.addView(connectionSummary,new LinearLayout.LayoutParams(0,-2,1f));

        trustBadgeBtn=PhoneUiKit.createButton(this,"Trust PC",palette,PhoneUiKit.BTN_SUBTLE);
        trustBadgeBtn.setTextSize(11f);
        trustBadgeBtn.setPadding(dp(10),dp(5),dp(10),dp(5));
        trustBadgeBtn.setOnClickListener(v->{
            DeskTouchSession current=DeskTouchRuntime.session(this);
            session=current;
            if(current.connectedFingerprint()!=null){
                current.requestTrust();
                setFeedback("Requested trust approval on connected Windows PC.",true);
            }else{
                selectTab(3);
            }
        });
        envTop.addView(trustBadgeBtn,new LinearLayout.LayoutParams(-2,-2));
        envCard.addView(envTop);

        environmentList=new LinearLayout(this);
        environmentList.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams envListLp=new LinearLayout.LayoutParams(-1,-2);
        envListLp.topMargin=dp(8);
        envCard.addView(environmentList,envListLp);

        LinearLayout protCard=PhoneUiKit.createCard(this,palette,14);
        protCard.setOrientation(LinearLayout.HORIZONTAL);
        protCard.setGravity(Gravity.CENTER_VERTICAL);
        page.addView(protCard,cardParams(10));

        LinearLayout protTextCol=new LinearLayout(this);
        protTextCol.setOrientation(LinearLayout.VERTICAL);
        TextView protTitle=new TextView(this);
        protTitle.setText("Control & Payment Protection");
        protTitle.setTextSize(13f);
        protTitle.setTypeface(null,Typeface.BOLD);
        protTextCol.addView(protTitle);

        protectionDesc=new TextView(this);
        protectionDesc.setText("Accessibility Ready • Virtual Bridge Ready");
        protectionDesc.setTextSize(11.5f);
        protectionDesc.setPadding(0,dp(2),0,0);
        protTextCol.addView(protectionDesc);

        LinearLayout.LayoutParams protColLp=new LinearLayout.LayoutParams(0,-2,1f);
        protColLp.rightMargin=dp(10);
        protCard.addView(protTextCol,protColLp);

        openPadBtn=PhoneUiKit.createButton(this,"Open Pad →",palette,PhoneUiKit.BTN_TINT_INDIGO);
        openPadBtn.setTextSize(12f);
        openPadBtn.setPadding(dp(12),dp(8),dp(12),dp(8));
        openPadBtn.setOnClickListener(v->selectTab(1));
        protCard.addView(openPadBtn,new LinearLayout.LayoutParams(-2,-2));

        onTheme(p->{
            PhoneUiKit.styleCard(hero,p,this);
            PhoneUiKit.styleButton(scanBtn,p,PhoneUiKit.BTN_SUBTLE,this);
            status.setTextColor(p.textPrimary);
            liveLinkView.setTextColor(p.accentText);
            PhoneUiKit.styleSubcard(mouseBox,p,this);
            PhoneUiKit.styleSubcard(kbBox,p,this);
            mLbl.setTextColor(p.textSecondary);
            kLbl.setTextColor(p.textSecondary);
            mouseMetric.setTextColor(p.textPrimary);
            kbMetric.setTextColor(p.textPrimary);
            PhoneUiKit.styleButton(returnControlBtn,p,PhoneUiKit.BTN_SUBTLE,this);
            PhoneUiKit.styleCard(envCard,p,this);
            connectionSummary.setTextColor(p.textSecondary);
            PhoneUiKit.styleCard(protCard,p,this);
            protTitle.setTextColor(p.textPrimary);
            protectionDesc.setTextColor(p.textSecondary);
            PhoneUiKit.styleButton(openPadBtn,p,PhoneUiKit.BTN_TINT_INDIGO,this);
            for(Runnable updater:dynamicDeviceThemeUpdaters)updater.run();
        });
    }

    private void buildTrackpadTab(LinearLayout page,int initialSensitivityProgress){
        LinearLayout padCard=PhoneUiKit.createCard(this,palette,14);
        page.addView(padCard,cardParams(0));

        LinearLayout padHeader=new LinearLayout(this);
        padHeader.setOrientation(LinearLayout.HORIZONTAL);
        padHeader.setGravity(Gravity.CENTER_VERTICAL);

        LinearLayout padTitleCol=new LinearLayout(this);
        padTitleCol.setOrientation(LinearLayout.VERTICAL);
        trackpadTargetTitle=new TextView(this);
        trackpadTargetTitle.setText("Touchpad Ready");
        trackpadTargetTitle.setTextSize(14f);
        trackpadTargetTitle.setTypeface(null,Typeface.BOLD);
        padTitleCol.addView(trackpadTargetTitle);

        trackpadReadout=new TextView(this);
        trackpadReadout.setText("Drag to move • Tap to click • 2 fingers scroll");
        trackpadReadout.setTextSize(11.5f);
        trackpadReadout.setPadding(0,dp(2),0,0);
        padTitleCol.addView(trackpadReadout);

        LinearLayout.LayoutParams padColLp=new LinearLayout.LayoutParams(0,-2,1f);
        padColLp.rightMargin=dp(8);
        padHeader.addView(padTitleCol,padColLp);

        captureMouseBtn=PhoneUiKit.createButton(this,"Capture Mouse",palette,PhoneUiKit.BTN_SUBTLE);
        captureMouseBtn.setTextSize(11.5f);
        captureMouseBtn.setPadding(dp(10),dp(7),dp(10),dp(7));
        captureMouseBtn.setOnClickListener(v->{
            pointerPad.requestFocus();
            pointerPad.requestPointerCapture();
            setFeedback("Physical mouse captured; use Back to release it.",true);
        });
        padHeader.addView(captureMouseBtn,new LinearLayout.LayoutParams(-2,-2));
        padCard.addView(padHeader);

        pointerPad=new View(this){
            final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG);
            final RectF bounds=new RectF();
            @Override protected void onDraw(Canvas c){
                PhoneUiKit.Palette p=palette;
                float r=dp(14);
                bounds.set(0,0,getWidth(),getHeight());
                paint.setStyle(Paint.Style.FILL);
                paint.setColor(p.touchpadBg);
                c.drawRoundRect(bounds,r,r,paint);

                paint.setColor(p.touchpadGrid);
                paint.setStrokeWidth(Math.max(1f,dp(1)));
                int step=dp(24);
                for(int gx=step;gx<getWidth();gx+=step)c.drawLine(gx,0,gx,getHeight(),paint);
                for(int gy=step;gy<getHeight();gy+=step)c.drawLine(0,gy,getWidth(),gy,paint);

                if(touchGlowActive){
                    paint.setColor(p.accentSoftBg);
                    c.drawCircle(touchGlowX,touchGlowY,dp(26),paint);
                    paint.setStyle(Paint.Style.STROKE);
                    paint.setStrokeWidth(Math.max(2f,dp(1)*1.5f));
                    paint.setColor(p.accentText);
                    c.drawCircle(touchGlowX,touchGlowY,dp(14),paint);
                }

                paint.setStyle(Paint.Style.STROKE);
                paint.setStrokeWidth(Math.max(1.5f,dp(1)));
                paint.setColor(p.touchpadBorder);
                c.drawRoundRect(0.5f,0.5f,getWidth()-0.5f,getHeight()-0.5f,r,r,paint);

                paint.setStyle(Paint.Style.FILL);
                paint.setTextAlign(Paint.Align.CENTER);
                paint.setColor(p.touchpadText);
                paint.setTextSize(dp(14));
                c.drawText("Interactive Touchpad Surface",getWidth()/2f,getHeight()/2f-dp(4),paint);
                paint.setColor(p.touchpadSubText);
                paint.setTextSize(dp(11));
                c.drawText("Move to navigate • tap to click • swipe edge to switch PC",
                    getWidth()/2f,getHeight()/2f+dp(16),paint);
            }
            @Override public boolean onCapturedPointerEvent(MotionEvent e){
                if(e.getActionMasked()==MotionEvent.ACTION_MOVE){
                    DeskTouchRuntime.session(MainActivity.this).moveBy(e.getX()*pointerSpeed,e.getY()*pointerSpeed);
                    return true;
                }
                if(e.getActionMasked()==MotionEvent.ACTION_SCROLL){
                    DeskTouchRuntime.session(MainActivity.this).wheel(Math.round(e.getAxisValue(MotionEvent.AXIS_VSCROLL)*120),false);
                    DeskTouchRuntime.session(MainActivity.this).wheel(Math.round(e.getAxisValue(MotionEvent.AXIS_HSCROLL)*120),true);
                    return true;
                }
                if(e.getActionMasked()==MotionEvent.ACTION_BUTTON_PRESS ||
                    e.getActionMasked()==MotionEvent.ACTION_BUTTON_RELEASE){
                    int button=e.getActionButton()==MotionEvent.BUTTON_SECONDARY?2:
                        e.getActionButton()==MotionEvent.BUTTON_TERTIARY?3:1;
                    DeskTouchRuntime.session(MainActivity.this).button(button,e.getActionMasked()==MotionEvent.ACTION_BUTTON_PRESS);
                    return true;
                }
                return super.onCapturedPointerEvent(e);
            }
        };
        pointerPad.setFocusable(true);
        pointerPad.setFocusableInTouchMode(true);
        LinearLayout.LayoutParams padLp=new LinearLayout.LayoutParams(-1,dp(220));
        padLp.topMargin=dp(10);
        padCard.addView(pointerPad,padLp);
        pointerPad.setOnTouchListener((v,event)->{
            switch(event.getActionMasked()){
                case MotionEvent.ACTION_DOWN:
                    if(v.getParent()!=null)v.getParent().requestDisallowInterceptTouchEvent(true);
                    prevX=downX=event.getX();prevY=downY=event.getY();
                    touchGlowX=event.getX();touchGlowY=event.getY();touchGlowActive=true;
                    v.invalidate();
                    dragged=false;twoFingerMoved=false;maxPointerCount=1;
                    twoFingerScrollCarry=0f;
                    v.requestFocus();return true;
                case MotionEvent.ACTION_POINTER_DOWN:
                    maxPointerCount=Math.max(maxPointerCount,event.getPointerCount());
                    if(event.getPointerCount()>=2){
                        twoFingerPrevY=(event.getY(0)+event.getY(1))*0.5f;
                        twoFingerScrollCarry=0f;
                    }
                    return true;
                case MotionEvent.ACTION_MOVE:
                    touchGlowX=event.getX();touchGlowY=event.getY();touchGlowActive=true;
                    v.invalidate();
                    if(event.getPointerCount()>=2){
                        maxPointerCount=Math.max(maxPointerCount,event.getPointerCount());
                        float avgY=(event.getY(0)+event.getY(1))*0.5f;
                        float sdy=avgY-twoFingerPrevY;
                        twoFingerPrevY=avgY;
                        if(Math.abs(sdy)>0.5f){
                            twoFingerScrollCarry+=sdy;
                            if(Math.abs(twoFingerScrollCarry)>dp(6))twoFingerMoved=true;
                            int step=dp(14);
                            while(Math.abs(twoFingerScrollCarry)>=step){
                                int wheelAmount=twoFingerScrollCarry>0 ? 120 : -120;
                                DeskTouchRuntime.session(MainActivity.this).wheel(wheelAmount,false);
                                twoFingerScrollCarry-=twoFingerScrollCarry>0 ? step : -step;
                            }
                        }
                        return true;
                    }
                    if(maxPointerCount>1)return true;
                    float dx=event.getX()-prevX,dy=event.getY()-prevY;
                    if(Math.abs(event.getX()-downX)+Math.abs(event.getY()-downY)>dp(9))
                        dragged=true;
                    DeskTouchRuntime.session(MainActivity.this).moveBy(dx*pointerSpeed,dy*pointerSpeed);
                    prevX=event.getX();prevY=event.getY();return true;
                case MotionEvent.ACTION_UP:
                    if(v.getParent()!=null)v.getParent().requestDisallowInterceptTouchEvent(false);
                    touchGlowActive=false;v.invalidate();
                    if(maxPointerCount>=2){
                        if(!twoFingerMoved)DeskTouchRuntime.session(MainActivity.this).click(2);
                    }else if(!dragged){
                        DeskTouchRuntime.session(MainActivity.this).click(1);
                    }
                    v.performClick();return true;
                case MotionEvent.ACTION_CANCEL:
                    if(v.getParent()!=null)v.getParent().requestDisallowInterceptTouchEvent(false);
                    touchGlowActive=false;v.invalidate();
                    return true;
                default:return true;
            }
        });

        keyboardInput=new EditText(this){
            @Override public InputConnection onCreateInputConnection(EditorInfo outAttrs){
                InputConnection base=super.onCreateInputConnection(outAttrs);
                if(outAttrs!=null){
                    outAttrs.imeOptions=(outAttrs.imeOptions&~EditorInfo.IME_MASK_ACTION)
                        | EditorInfo.IME_ACTION_NONE | EditorInfo.IME_FLAG_NO_EXTRACT_UI;
                }
                if(base==null)return null;
                return new InputConnectionWrapper(base,true){
                    @Override public boolean deleteSurroundingText(int beforeLength,int afterLength){
                        if(getText()!=null && getText().length()==0 && beforeLength>0){
                            DeskTouchSession s=DeskTouchRuntime.session(MainActivity.this);
                            if(s.isActive()){
                                s.key(0x08,true);s.key(0x08,false);
                                return true;
                            }
                        }
                        return super.deleteSurroundingText(beforeLength,afterLength);
                    }
                    @Override public boolean sendKeyEvent(KeyEvent event){
                        if(event!=null && event.getAction()==KeyEvent.ACTION_DOWN &&
                            event.getKeyCode()==KeyEvent.KEYCODE_DEL &&
                            getText()!=null && getText().length()==0){
                            DeskTouchSession s=DeskTouchRuntime.session(MainActivity.this);
                            if(s.isActive()){
                                s.key(0x08,true);s.key(0x08,false);
                                return true;
                            }
                        }
                        return super.sendKeyEvent(event);
                    }
                };
            }
        };
        keyboardInput.setHint("Type on selected computer...");
        keyboardInput.setSingleLine(false);
        keyboardInput.setTextSize(13f);
        keyboardInput.setPadding(dp(12),dp(10),dp(12),dp(10));
        keyboardInput.setMinHeight(dp(44));
        keyboardInput.setInputType(android.text.InputType.TYPE_CLASS_TEXT|
            android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE);

        LinearLayout mouseActions=new LinearLayout(this);
        mouseActions.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams maLp=new LinearLayout.LayoutParams(-1,-2);
        maLp.topMargin=dp(10);
        padCard.addView(mouseActions,maLp);

        Button leftBtn=addPadActionButton(mouseActions,"Left",PhoneUiKit.BTN_SUBTLE,true,
            ()->DeskTouchRuntime.session(MainActivity.this).click(1));
        Button rightBtn=addPadActionButton(mouseActions,"Right",PhoneUiKit.BTN_SUBTLE,false,
            ()->DeskTouchRuntime.session(MainActivity.this).click(2));
        Button upBtn=addPadActionButton(mouseActions,"↑",PhoneUiKit.BTN_SUBTLE,false,
            ()->DeskTouchRuntime.session(MainActivity.this).wheel(120,false));
        Button downBtn=addPadActionButton(mouseActions,"↓",PhoneUiKit.BTN_SUBTLE,false,
            ()->DeskTouchRuntime.session(MainActivity.this).wheel(-120,false));
        Button keysBtn=addPadActionButton(mouseActions,"Keys",PhoneUiKit.BTN_TINT_ACCENT,false,()->{
            keyboardInput.requestFocus();
            InputMethodManager imm=(InputMethodManager)getSystemService(Context.INPUT_METHOD_SERVICE);
            if(imm!=null)imm.showSoftInput(keyboardInput,InputMethodManager.SHOW_IMPLICIT);
        });

        LinearLayout kbCard=PhoneUiKit.createCard(this,palette,14);
        page.addView(kbCard,cardParams(10));

        LinearLayout kbTop=new LinearLayout(this);
        kbTop.setOrientation(LinearLayout.HORIZONTAL);
        kbTop.setGravity(Gravity.CENTER_VERTICAL);
        TextView kbTitle=new TextView(this);
        kbTitle.setText("Remote Keyboard Box");
        kbTitle.setTextSize(12.5f);
        kbTitle.setTypeface(null,Typeface.BOLD);
        kbTop.addView(kbTitle,new LinearLayout.LayoutParams(0,-2,1f));

        final String[] previousTyped={""};
        TextView clearBtn=new TextView(this);
        clearBtn.setText("Clear");
        clearBtn.setTextSize(12f);
        clearBtn.setTypeface(null,Typeface.BOLD);
        clearBtn.setPadding(dp(8),dp(4),dp(4),dp(4));
        clearBtn.setClickable(true);
        clearBtn.setFocusable(true);
        clearBtn.setOnClickListener(v->{
            suppressTypedText=true;
            keyboardInput.setText("");
            previousTyped[0]="";
            suppressTypedText=false;
        });
        kbTop.addView(clearBtn);
        kbCard.addView(kbTop);

        LinearLayout.LayoutParams inputLp=new LinearLayout.LayoutParams(-1,-2);
        inputLp.topMargin=dp(8);
        kbCard.addView(keyboardInput,inputLp);

        keyboardInput.addTextChangedListener(new TextWatcher(){
            public void beforeTextChanged(CharSequence text,int start,int count,int after){}
            public void onTextChanged(CharSequence text,int start,int before,int count){}
            public void afterTextChanged(Editable editable){
                if(suppressTypedText)return;
                String next=editable.toString();
                DeskTouchSession active=DeskTouchRuntime.session(MainActivity.this);
                if(active.isActive()){
                    int common=0;
                    while(common<previousTyped[0].length()&&common<next.length()&&
                        previousTyped[0].charAt(common)==next.charAt(common))common++;
                    for(int n=common;n<previousTyped[0].length();n++){
                        active.key(8,true);active.key(8,false);
                    }
                    if(common<next.length())sendRemoteTextWithNewlines(active,next.substring(common));
                }
                previousTyped[0]=next;
                if(next.length()>800){
                    suppressTypedText=true;
                    editable.clear();previousTyped[0]="";
                    suppressTypedText=false;
                }
            }
        });

        keyboardBindingsView=new TextView(this);
        keyboardBindingsView.setTextSize(11.5f);
        keyboardBindingsView.setPadding(0,dp(8),0,0);
        kbCard.addView(keyboardBindingsView);

        LinearLayout sensCard=PhoneUiKit.createCard(this,palette,14);
        page.addView(sensCard,cardParams(10));

        LinearLayout sensTop=new LinearLayout(this);
        sensTop.setOrientation(LinearLayout.HORIZONTAL);
        sensTop.setGravity(Gravity.CENTER_VERTICAL);
        TextView sensTitle=new TextView(this);
        sensTitle.setText("Pointer Sensitivity");
        sensTitle.setTextSize(12.5f);
        sensTitle.setTypeface(null,Typeface.BOLD);
        sensTop.addView(sensTitle,new LinearLayout.LayoutParams(0,-2,1f));

        sensReadout=new TextView(this);
        sensReadout.setText(Math.round(pointerSpeed*100f/2.8f)+"%");
        sensReadout.setTextSize(13f);
        sensReadout.setTypeface(null,Typeface.BOLD);
        sensTop.addView(sensReadout);
        sensCard.addView(sensTop);

        pointerSensitivity=new SeekBar(this);
        pointerSensitivity.setMax(400);
        pointerSensitivity.setProgress(initialSensitivityProgress);
        pointerSensitivity.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener(){
            public void onProgressChanged(SeekBar b,int progress,boolean fromUser){
                pointerSpeed=1f+progress/100f;
                sensReadout.setText(Math.round(pointerSpeed*100f/2.8f)+"%");
                if(fromUser){
                    getSharedPreferences(PREFS,MODE_PRIVATE).edit()
                        .putInt(PREF_POINTER_PROGRESS,progress).apply();
                }
            }
            public void onStartTrackingTouch(SeekBar b){}
            public void onStopTrackingTouch(SeekBar b){}
        });
        LinearLayout.LayoutParams seekLp=new LinearLayout.LayoutParams(-1,-2);
        seekLp.topMargin=dp(8);
        sensCard.addView(pointerSensitivity,seekLp);

        onTheme(p->{
            PhoneUiKit.styleCard(padCard,p,this);
            trackpadTargetTitle.setTextColor(p.textPrimary);
            trackpadReadout.setTextColor(p.textSecondary);
            PhoneUiKit.styleButton(captureMouseBtn,p,PhoneUiKit.BTN_SUBTLE,this);
            pointerPad.invalidate();
            PhoneUiKit.styleButton(leftBtn,p,PhoneUiKit.BTN_SUBTLE,this);
            PhoneUiKit.styleButton(rightBtn,p,PhoneUiKit.BTN_SUBTLE,this);
            PhoneUiKit.styleButton(upBtn,p,PhoneUiKit.BTN_SUBTLE,this);
            PhoneUiKit.styleButton(downBtn,p,PhoneUiKit.BTN_SUBTLE,this);
            PhoneUiKit.styleButton(keysBtn,p,PhoneUiKit.BTN_TINT_ACCENT,this);
            PhoneUiKit.styleCard(kbCard,p,this);
            kbTitle.setTextColor(p.textPrimary);
            clearBtn.setTextColor(p.accentText);
            PhoneUiKit.styleSubcard(keyboardInput,p,this);
            keyboardInput.setTextColor(p.textPrimary);
            keyboardInput.setHintTextColor(p.textMuted);
            keyboardBindingsView.setTextColor(p.textSecondary);
            PhoneUiKit.styleCard(sensCard,p,this);
            sensTitle.setTextColor(p.textPrimary);
            sensReadout.setTextColor(p.accentText);
            pointerSensitivity.setProgressTintList(ColorStateList.valueOf(p.accent));
            pointerSensitivity.setProgressBackgroundTintList(ColorStateList.valueOf(p.switchTrackOff));
            pointerSensitivity.setThumbTintList(ColorStateList.valueOf(p.accent));
        });
    }

    private void buildSpeedTab(LinearLayout page){
        LinearLayout compRow=new LinearLayout(this);
        compRow.setOrientation(LinearLayout.HORIZONTAL);
        page.addView(compRow,new LinearLayout.LayoutParams(-1,-2));

        LinearLayout wifiCard=PhoneUiKit.createCard(this,palette,12);
        LinearLayout.LayoutParams wLp=new LinearLayout.LayoutParams(0,-2,1f);
        wLp.rightMargin=dp(5);
        compRow.addView(wifiCard,wLp);

        LinearLayout wTop=new LinearLayout(this);
        wTop.setOrientation(LinearLayout.HORIZONTAL);
        wTop.setGravity(Gravity.CENTER_VERTICAL);
        TextView wTitle=new TextView(this);
        wTitle.setText("Wireless Link");
        wTitle.setTextSize(12f);
        wTitle.setTypeface(null,Typeface.BOLD);
        wTop.addView(wTitle,new LinearLayout.LayoutParams(0,-2,1f));
        wifiLossBadge=new TextView(this);
        wifiLossBadge.setText("Offline");
        wifiLossBadge.setTextSize(10f);
        wifiLossBadge.setTypeface(null,Typeface.BOLD);
        wifiLossBadge.setPadding(dp(6),dp(2),dp(6),dp(2));
        wTop.addView(wifiLossBadge);
        wifiCard.addView(wTop);

        speedWifiRtt=new TextView(this);
        speedWifiRtt.setText("—");
        speedWifiRtt.setTextSize(17f);
        speedWifiRtt.setTypeface(null,Typeface.BOLD);
        speedWifiRtt.setPadding(0,dp(5),0,dp(2));
        wifiCard.addView(speedWifiRtt);

        speedWifiSub=new TextView(this);
        speedWifiSub.setText("Waiting for wireless");
        speedWifiSub.setTextSize(11f);
        speedWifiSub.setMaxLines(2);
        wifiCard.addView(speedWifiSub);

        LinearLayout mobCard=PhoneUiKit.createCard(this,palette,12);
        LinearLayout.LayoutParams mLp=new LinearLayout.LayoutParams(0,-2,1f);
        mLp.leftMargin=dp(5);
        compRow.addView(mobCard,mLp);

        LinearLayout mTop=new LinearLayout(this);
        mTop.setOrientation(LinearLayout.HORIZONTAL);
        mTop.setGravity(Gravity.CENTER_VERTICAL);
        TextView mTitle=new TextView(this);
        mTitle.setText("Mobile Network");
        mTitle.setTextSize(12f);
        mTitle.setTypeface(null,Typeface.BOLD);
        mTop.addView(mTitle,new LinearLayout.LayoutParams(0,-2,1f));
        mobLossBadge=new TextView(this);
        mobLossBadge.setText("Standby");
        mobLossBadge.setTextSize(10f);
        mobLossBadge.setTypeface(null,Typeface.BOLD);
        mobLossBadge.setPadding(dp(6),dp(2),dp(6),dp(2));
        mTop.addView(mobLossBadge);
        mobCard.addView(mTop);

        speedMobRtt=new TextView(this);
        speedMobRtt.setText("—");
        speedMobRtt.setTextSize(17f);
        speedMobRtt.setTypeface(null,Typeface.BOLD);
        speedMobRtt.setPadding(0,dp(5),0,dp(2));
        mobCard.addView(speedMobRtt);

        speedMobSub=new TextView(this);
        speedMobSub.setText("Ready alongside Wi-Fi");
        speedMobSub.setTextSize(11f);
        speedMobSub.setMaxLines(2);
        mobCard.addView(speedMobSub);

        LinearLayout segCard=PhoneUiKit.createCard(this,palette,14);
        page.addView(segCard,cardParams(10));

        addSegHeader(segCard,"Mouse Channel","Switches only on 1%+ loss",false);
        mouseModeSeg=new PhoneUiKit.SegmentedControl(this,palette,
            new String[]{"Auto","Mobile","Wireless"},
            PhoneUiKit.mouseModeToSegment(DeskTouchSession.readMouseMode(this)),
            idx->{
                DeskTouchSession.writeMouseMode(this,PhoneUiKit.segmentToMouseMode(idx));
                DeskTouchRuntime.applyStreamOptions(this);
                refreshStreamOptionControls();
                refreshLiveLinkAndKeyboard();
            });
        segCard.addView(mouseModeSeg.getView(),segParams());

        addSegHeader(segCard,"Keyboard Channel","Picks lowest ping 0%-loss",true);
        keyboardModeSeg=new PhoneUiKit.SegmentedControl(this,palette,
            new String[]{"Fastest","Wireless","Mobile"},
            PhoneUiKit.keyboardModeToSegment(DeskTouchSession.readKeyboardMode(this)),
            idx->{
                DeskTouchSession.writeKeyboardMode(this,PhoneUiKit.segmentToKeyboardMode(idx));
                DeskTouchRuntime.applyStreamOptions(this);
                refreshStreamOptionControls();
                refreshLiveLinkAndKeyboard();
            });
        segCard.addView(keyboardModeSeg.getView(),segParams());

        addSegHeader(segCard,"Cursor Feel","Smart anti-jitter filter",true);
        pointerFeelSeg=new PhoneUiKit.SegmentedControl(this,palette,
            new String[]{"Auto","Raw 0ms","Smooth"},
            PhoneUiKit.pointerFeelToSegment(DeskTouchSession.readPointerFeelMode(this)),
            idx->{
                DeskTouchSession.writePointerFeelMode(this,PhoneUiKit.segmentToPointerFeel(idx));
                DeskTouchRuntime.applyStreamOptions(this);
                refreshStreamOptionControls();
            });
        segCard.addView(pointerFeelSeg.getView(),segParams());

        LinearLayout boostCard=PhoneUiKit.createCard(this,palette,14);
        page.addView(boostCard,cardParams(10));

        dualPathControlsSwitch=new PhoneUiKit.SwitchRow(this,palette,
            "Dual-path keys & clicks",
            "Send over both paths at once; first packet wins",
            DeskTouchSession.readDualPathControls(this),
            checked->{
                DeskTouchSession.writeDualPathControls(this,checked);
                DeskTouchRuntime.applyStreamOptions(this);
                refreshStreamOptionControls();
                refreshLiveLinkAndKeyboard();
            });
        boostCard.addView(dualPathControlsSwitch.getView());

        View boostDiv=PhoneUiKit.createDivider(this,palette);
        boostCard.addView(boostDiv);

        keepChannelsWarmSwitch=new PhoneUiKit.SwitchRow(this,palette,
            "Keep wireless radio awake",
            "Tiny wake pulses stop typing lag between pauses",
            DeskTouchSession.readKeepChannelsWarm(this),
            checked->{
                DeskTouchSession.writeKeepChannelsWarm(this,checked);
                DeskTouchRuntime.applyStreamOptions(this);
                refreshStreamOptionControls();
            });
        boostCard.addView(keepChannelsWarmSwitch.getView());

        LinearLayout actionRow=new LinearLayout(this);
        actionRow.setOrientation(LinearLayout.HORIZONTAL);
        page.addView(actionRow,cardParams(10));

        shortcutsBtn=PhoneUiKit.createButton(this,"Keyboard Shortcuts",palette,PhoneUiKit.BTN_SUBTLE);
        shortcutsBtn.setOnClickListener(v->showKeyboardShortcutsDialog());
        LinearLayout.LayoutParams sLp=new LinearLayout.LayoutParams(0,-2,1f);
        sLp.rightMargin=dp(4);
        actionRow.addView(shortcutsBtn,sLp);

        resetPathsBtn=PhoneUiKit.createButton(this,"Reset Saved Paths",palette,PhoneUiKit.BTN_SUBTLE);
        resetPathsBtn.setOnClickListener(v->{
            DeskTouchRuntime.resetRememberedStreams(this);
            refreshStreamOptionControls();
            refreshLiveLinkAndKeyboard();
            showToast("Cleared remembered network paths.");
        });
        LinearLayout.LayoutParams rLp=new LinearLayout.LayoutParams(0,-2,1f);
        rLp.leftMargin=dp(4);
        actionRow.addView(resetPathsBtn,rLp);

        onTheme(p->{
            PhoneUiKit.styleCard(wifiCard,p,this);
            PhoneUiKit.styleCard(mobCard,p,this);
            wTitle.setTextColor(p.textPrimary);
            mTitle.setTextColor(p.textPrimary);
            speedWifiRtt.setTextColor(p.textPrimary);
            speedMobRtt.setTextColor(p.textPrimary);
            speedWifiSub.setTextColor(p.textSecondary);
            speedMobSub.setTextColor(p.textSecondary);
            PhoneUiKit.styleCard(segCard,p,this);
            mouseModeSeg.applyPalette(p);
            keyboardModeSeg.applyPalette(p);
            pointerFeelSeg.applyPalette(p);
            PhoneUiKit.styleCard(boostCard,p,this);
            dualPathControlsSwitch.applyPalette(p);
            boostDiv.setBackgroundColor(p.divider);
            keepChannelsWarmSwitch.applyPalette(p);
            PhoneUiKit.styleButton(shortcutsBtn,p,PhoneUiKit.BTN_SUBTLE,this);
            PhoneUiKit.styleButton(resetPathsBtn,p,PhoneUiKit.BTN_SUBTLE,this);
        });
    }

    private void buildSetupTab(LinearLayout page){
        LinearLayout safetyCard=PhoneUiKit.createCard(this,palette,14);
        page.addView(safetyCard,cardParams(0));

        LinearLayout safetyTop=new LinearLayout(this);
        safetyTop.setOrientation(LinearLayout.HORIZONTAL);
        safetyTop.setGravity(Gravity.CENTER_VERTICAL);
        TextView safetyTitle=PhoneUiKit.createSectionLabel(this,"CONTROL & PAYMENT SAFETY",palette);
        safetyTop.addView(safetyTitle,new LinearLayout.LayoutParams(0,-2,1f));
        safetyBadge=new TextView(this);
        safetyBadge.setText("Ready");
        safetyBadge.setTextSize(11f);
        safetyBadge.setTypeface(null,Typeface.BOLD);
        safetyBadge.setPadding(dp(8),dp(3),dp(8),dp(3));
        safetyTop.addView(safetyBadge);
        safetyCard.addView(safetyTop);

        allowWindowsSwitch=new PhoneUiKit.SwitchRow(this,palette,
            "Allow Windows control",
            "Approved computers can steer this screen",
            DeskTouchAccessibilityService.allowed(this),
            enabled->{
                DeskTouchAccessibilityService.setAllowed(this,enabled);
                DeskTouchRuntime.accessibilityPermissionChanged(this);
                refreshPermissionState();
                updateVisibleScreenAwake();
                if(enabled && !DeskTouchAccessibilityService.ready(this)){
                    setFeedback("Enable 'DeskTouch Windows control' in Android Accessibility settings.",true);
                    DeskTouchAccessibilityService.openAccessibilitySettings(this,true);
                }else{
                    setFeedback(enabled
                        ? "Windows control enabled."
                        : "Windows control disabled. Android touch has priority.",true);
                }
            });
        LinearLayout.LayoutParams sw1Lp=new LinearLayout.LayoutParams(-1,-2);
        sw1Lp.topMargin=dp(8);
        safetyCard.addView(allowWindowsSwitch.getView(),sw1Lp);

        View sd1=PhoneUiKit.createDivider(this,palette);
        safetyCard.addView(sd1);

        allowShellBridgeSwitch=new PhoneUiKit.SwitchRow(this,palette,
            "Virtual Bridge (Accessibility-free)",
            "Works automatically over cable or Wireless Debugging",
            ShellInputClient.allowed(this),
            enabled->{
                ShellInputClient.setAllowed(this,enabled);
                refreshPermissionState();
                updateVisibleScreenAwake();
                setFeedback(enabled
                    ? (ShellInputClient.ready(this)
                        ? "Accessibility-free Virtual Bridge enabled and ready."
                        : "Virtual Bridge enabled. Plug in the data cable while DeskTouch is running on Windows to start it without Accessibility.")
                    : "Virtual Bridge disabled in DeskTouch.",true);
            });
        safetyCard.addView(allowShellBridgeSwitch.getView());

        View sd2=PhoneUiKit.createDivider(this,palette);
        safetyCard.addView(sd2);

        autoPausePaymentsSwitch=new PhoneUiKit.SwitchRow(this,palette,
            "Auto-pause in UPI & bank apps",
            "Turns off Accessibility during payments",
            DeskTouchAccessibilityService.autoPausePaymentAppsEnabled(this),
            checked->{
                DeskTouchAccessibilityService.setAutoPausePaymentApps(this,checked);
                setFeedback(checked
                    ? "DeskTouch will automatically turn off Accessibility when you open UPI/payment or bank apps."
                    : "Automatic payment-app pause turned off; use the notification, widget, or Quick Tile instead.",true);
            });
        safetyCard.addView(autoPausePaymentsSwitch.getView());

        View sd3=PhoneUiKit.createDivider(this,palette);
        safetyCard.addView(sd3);

        autoPauseDisconnectSwitch=new PhoneUiKit.SwitchRow(this,palette,
            "Auto-pause 15s after disconnect",
            "Cleanly turns off Accessibility when idle",
            DeskTouchAccessibilityService.autoPauseOnDisconnectEnabled(this),
            checked->{
                DeskTouchAccessibilityService.setAutoPauseOnDisconnect(this,checked);
                setFeedback(checked
                    ? "Accessibility will automatically turn off 15 seconds after leaving your Windows PC."
                    : "Accessibility will remain on after Windows disconnects until you turn it off.",true);
            });
        safetyCard.addView(autoPauseDisconnectSwitch.getView());

        LinearLayout permBox=PhoneUiKit.createSubcard(this,palette,10);
        LinearLayout.LayoutParams permBoxLp=new LinearLayout.LayoutParams(-1,-2);
        permBoxLp.topMargin=dp(10);
        safetyCard.addView(permBox,permBoxLp);

        permissionState=new TextView(this);
        permissionState.setTextSize(11.5f);
        permissionState.setLineSpacing(dp(2),1f);
        permBox.addView(permissionState);

        LinearLayout safetyBtns1=new LinearLayout(this);
        safetyBtns1.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams sb1Lp=new LinearLayout.LayoutParams(-1,-2);
        sb1Lp.topMargin=dp(10);
        safetyCard.addView(safetyBtns1,sb1Lp);

        Button accBtn=PhoneUiKit.createButton(this,"Accessibility Settings",palette,PhoneUiKit.BTN_SUBTLE);
        accBtn.setOnClickListener(v->DeskTouchAccessibilityService.openAccessibilitySettings(
            this,!PermissionContinuity.systemAccessibilityEnabled(this)));
        LinearLayout.LayoutParams accLp=new LinearLayout.LayoutParams(0,-2,1f);
        accLp.rightMargin=dp(4);
        safetyBtns1.addView(accBtn,accLp);

        Button pinWidgetBtn=PhoneUiKit.createButton(this,"Pin Pay Widget",palette,PhoneUiKit.BTN_SUBTLE);
        pinWidgetBtn.setOnClickListener(v->{
            boolean requested=PaymentPauseWidgetProvider.requestPin(this);
            setFeedback(requested
                ? "Confirm adding the DeskTouch Pay Mode button on your home screen."
                : "Long-press your Home screen -> Widgets -> add DeskTouch Pay Mode.",true);
        });
        LinearLayout.LayoutParams pinLp=new LinearLayout.LayoutParams(0,-2,1f);
        pinLp.leftMargin=dp(4);
        safetyBtns1.addView(pinWidgetBtn,pinLp);

        Button stopCtrlBtn=PhoneUiKit.createButton(this,"Stop Windows Control",palette,PhoneUiKit.BTN_DANGER);
        stopCtrlBtn.setOnClickListener(v->{
            DeskTouchRuntime.remoteNativeTouch();
            setFeedback("Windows input authority returned to Android.",true);
        });
        LinearLayout.LayoutParams stopLp=new LinearLayout.LayoutParams(-1,-2);
        stopLp.topMargin=dp(8);
        safetyCard.addView(stopCtrlBtn,stopLp);

        LinearLayout kbXiaomiCard=PhoneUiKit.createCard(this,palette,14);
        page.addView(kbXiaomiCard,cardParams(10));

        LinearLayout kxTop=new LinearLayout(this);
        kxTop.setOrientation(LinearLayout.HORIZONTAL);
        kxTop.setGravity(Gravity.CENTER_VERTICAL);
        TextView kxTitle=PhoneUiKit.createSectionLabel(this,"KEYBOARD & XIAOMI POWER",palette);
        kxTop.addView(kxTitle,new LinearLayout.LayoutParams(0,-2,1f));
        TextView xiaomiTipBadge=new TextView(this);
        xiaomiTipBadge.setText("Xiaomi Tip");
        xiaomiTipBadge.setTextSize(11f);
        xiaomiTipBadge.setTypeface(null,Typeface.BOLD);
        xiaomiTipBadge.setPadding(dp(8),dp(3),dp(8),dp(3));
        kxTop.addView(xiaomiTipBadge);
        kbXiaomiCard.addView(kxTop);

        TextView xiaomiNote=new TextView(this);
        xiaomiNote.setText("Join one DeskTouch environment hub on your Wi-Fi without developer mode or cables. "
            +"Xiaomi / Redmi setup tip: in App Info -> tap 3 dots at top-right -> Allow restricted settings, "
            +"and set Battery to No restrictions so control and auto-reconnect stay instant.");
        xiaomiNote.setTextSize(11.5f);
        xiaomiNote.setLineSpacing(dp(2),1f);
        xiaomiNote.setPadding(0,dp(8),0,dp(10));
        kbXiaomiCard.addView(xiaomiNote);

        LinearLayout kxRow1=new LinearLayout(this);
        kxRow1.setOrientation(LinearLayout.HORIZONTAL);
        kbXiaomiCard.addView(kxRow1,new LinearLayout.LayoutParams(-1,-2));

        // A dedicated input method improves editor typing (including WebViews)
        // without requiring root, developer mode or a Bluetooth pairing.
        Button enableKbBtn=PhoneUiKit.createButton(this,"Enable Keyboard",palette,PhoneUiKit.BTN_SUBTLE);
        enableKbBtn.setOnClickListener(v->startActivity(new Intent(Settings.ACTION_INPUT_METHOD_SETTINGS)));
        LinearLayout.LayoutParams ekLp=new LinearLayout.LayoutParams(0,-2,1f);
        ekLp.rightMargin=dp(4);
        kxRow1.addView(enableKbBtn,ekLp);

        Button selectKbBtn=PhoneUiKit.createButton(this,"Select Keyboard",palette,PhoneUiKit.BTN_SUBTLE);
        selectKbBtn.setOnClickListener(v->{
            InputMethodManager manager=(InputMethodManager)getSystemService(INPUT_METHOD_SERVICE);
            if(manager!=null)manager.showInputMethodPicker();
        });
        LinearLayout.LayoutParams skLp=new LinearLayout.LayoutParams(0,-2,1f);
        skLp.leftMargin=dp(4);
        kxRow1.addView(selectKbBtn,skLp);

        LinearLayout kxRow2=new LinearLayout(this);
        kxRow2.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams kx2Lp=new LinearLayout.LayoutParams(-1,-2);
        kx2Lp.topMargin=dp(8);
        kbXiaomiCard.addView(kxRow2,kx2Lp);

        Button androidKbBtn=PhoneUiKit.createButton(this,"Android Keyboard Settings",palette,PhoneUiKit.BTN_SUBTLE);
        androidKbBtn.setOnClickListener(v->startActivity(new Intent(Settings.ACTION_INPUT_METHOD_SETTINGS)));
        LinearLayout.LayoutParams akLp=new LinearLayout.LayoutParams(0,-2,1f);
        akLp.rightMargin=dp(4);
        kxRow2.addView(androidKbBtn,akLp);

        Button xiaomiBatBtn=PhoneUiKit.createButton(this,"Open App Info & Xiaomi Battery",palette,PhoneUiKit.BTN_SUBTLE);
        xiaomiBatBtn.setOnClickListener(v->startActivity(new Intent(
            Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
            android.net.Uri.fromParts("package",getPackageName(),null))));
        LinearLayout.LayoutParams xbLp=new LinearLayout.LayoutParams(0,-2,1f);
        xbLp.leftMargin=dp(4);
        kxRow2.addView(xiaomiBatBtn,xbLp);

        LinearLayout trustCard=PhoneUiKit.createCard(this,palette,14);
        page.addView(trustCard,cardParams(10));

        TextView trustTitle=PhoneUiKit.createSectionLabel(this,"TRUST & UPDATES",palette);
        trustCard.addView(trustTitle);

        LinearLayout trustBox=PhoneUiKit.createSubcard(this,palette,10);
        LinearLayout.LayoutParams tbLp=new LinearLayout.LayoutParams(-1,-2);
        tbLp.topMargin=dp(8);
        trustCard.addView(trustBox,tbLp);

        trustSummary=new TextView(this);
        trustSummary.setTextSize(12f);
        trustBox.addView(trustSummary);

        autoStartSwitch=new PhoneUiKit.SwitchRow(this,palette,
            "Auto-launch & reconnect",
            "Rejoins your trusted computers on Android startup",
            AutoConnectService.enabled(this),
            checked->{
                AutoConnectService.setEnabled(this,checked);
                AutoConnectService.setReconnect(this,checked);
                setFeedback(checked
                    ? "Android startup enabled; trusted PCs reconnect automatically when reachable."
                    : "Android startup and background reconnect disabled.",true);
            });
        LinearLayout.LayoutParams asLp=new LinearLayout.LayoutParams(-1,-2);
        asLp.topMargin=dp(8);
        trustCard.addView(autoStartSwitch.getView(),asLp);

        Button trustPcBtn=PhoneUiKit.createButton(this,"Trust Connected PC",palette,PhoneUiKit.BTN_TINT_EMERALD);
        trustPcBtn.setOnClickListener(v->{
            DeskTouchSession current=DeskTouchRuntime.session(this);
            session=current;
            current.requestTrust();
            setFeedback("Trust request sent to connected Windows PC.",true);
        });
        LinearLayout.LayoutParams tpLp=new LinearLayout.LayoutParams(-1,-2);
        tpLp.topMargin=dp(10);
        trustCard.addView(trustPcBtn,tpLp);

        LinearLayout tuRow1=new LinearLayout(this);
        tuRow1.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams tu1Lp=new LinearLayout.LayoutParams(-1,-2);
        tu1Lp.topMargin=dp(8);
        trustCard.addView(tuRow1,tu1Lp);

        Button checkUpdBtn=PhoneUiKit.createButton(this,"Check Updates",palette,PhoneUiKit.BTN_SUBTLE);
        checkUpdBtn.setOnClickListener(v->AndroidUpdater.checkForUpdates(this,true));
        LinearLayout.LayoutParams cuLp=new LinearLayout.LayoutParams(0,-2,1f);
        cuLp.rightMargin=dp(4);
        tuRow1.addView(checkUpdBtn,cuLp);

        Button installUpdBtn=PhoneUiKit.createButton(this,"Update App",palette,PhoneUiKit.BTN_TINT_ACCENT);
        installUpdBtn.setOnClickListener(v->AndroidUpdater.installAvailableUpdate(this));
        LinearLayout.LayoutParams iuLp=new LinearLayout.LayoutParams(0,-2,1f);
        iuLp.leftMargin=dp(4);
        tuRow1.addView(installUpdBtn,iuLp);

        LinearLayout tuRow2=new LinearLayout(this);
        tuRow2.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams tu2Lp=new LinearLayout.LayoutParams(-1,-2);
        tu2Lp.topMargin=dp(8);
        trustCard.addView(tuRow2,tu2Lp);

        Button leaveBtn=PhoneUiKit.createButton(this,"Leave Group",palette,PhoneUiKit.BTN_SUBTLE);
        leaveBtn.setOnClickListener(v->{
            DeskTouchSession current=DeskTouchRuntime.session(this);
            session=current;
            AutoConnectService.pausePeer(this,DeskTouchRuntime.selectedIdentity());
            current.releaseControl();
            current.disconnect();
            refreshTrustSummary();
            refreshEnvironmentMembers();
            refreshLiveLinkAndKeyboard();
            setFeedback("Selected device disconnected. Other peers stay connected; "
                +"auto reconnect paused only for this device.",true);
        });
        LinearLayout.LayoutParams lgLp=new LinearLayout.LayoutParams(0,-2,1f);
        lgLp.rightMargin=dp(4);
        tuRow2.addView(leaveBtn,lgLp);

        Button forgetBtn=PhoneUiKit.createButton(this,"Forget Trusted",palette,PhoneUiKit.BTN_DANGER);
        forgetBtn.setOnClickListener(v->{
            new TrustedWindows(this).clearAll();
            refreshTrustSummary();
            setFeedback("Cleared saved trusted computers on this phone.",true);
        });
        LinearLayout.LayoutParams fgLp=new LinearLayout.LayoutParams(0,-2,1f);
        fgLp.leftMargin=dp(4);
        tuRow2.addView(forgetBtn,fgLp);

        LinearLayout appearCard=PhoneUiKit.createCard(this,palette,14);
        page.addView(appearCard,cardParams(10));

        TextView appearTitle=PhoneUiKit.createSectionLabel(this,"APPEARANCE",palette);
        appearCard.addView(appearTitle);

        TextView appearSub=new TextView(this);
        appearSub.setText("Switch themes smoothly without dropping active connections");
        appearSub.setTextSize(11.5f);
        appearSub.setPadding(0,dp(4),0,dp(8));
        appearCard.addView(appearSub);

        themeSeg=new PhoneUiKit.SegmentedControl(this,palette,
            new String[]{"Midnight Slate","AMOLED Black","Crisp Light"},
            palette.themeId,
            idx->{
                PhoneUiKit.writeTheme(this,idx);
                applyTheme(idx);
            });
        appearCard.addView(themeSeg.getView(),new LinearLayout.LayoutParams(-1,-2));

        onTheme(p->{
            PhoneUiKit.styleCard(safetyCard,p,this);
            safetyTitle.setTextColor(p.textSecondary);
            allowWindowsSwitch.applyPalette(p);
            sd1.setBackgroundColor(p.divider);
            allowShellBridgeSwitch.applyPalette(p);
            sd2.setBackgroundColor(p.divider);
            autoPausePaymentsSwitch.applyPalette(p);
            sd3.setBackgroundColor(p.divider);
            autoPauseDisconnectSwitch.applyPalette(p);
            PhoneUiKit.styleSubcard(permBox,p,this);
            permissionState.setTextColor(p.amberText);
            PhoneUiKit.styleButton(accBtn,p,PhoneUiKit.BTN_SUBTLE,this);
            PhoneUiKit.styleButton(pinWidgetBtn,p,PhoneUiKit.BTN_SUBTLE,this);
            PhoneUiKit.styleButton(stopCtrlBtn,p,PhoneUiKit.BTN_DANGER,this);

            PhoneUiKit.styleCard(kbXiaomiCard,p,this);
            kxTitle.setTextColor(p.textSecondary);
            PhoneUiKit.stylePill(xiaomiTipBadge,p.amberSoftBg,Color.TRANSPARENT,p.amberText,this);
            xiaomiNote.setTextColor(p.textSecondary);
            PhoneUiKit.styleButton(enableKbBtn,p,PhoneUiKit.BTN_SUBTLE,this);
            PhoneUiKit.styleButton(selectKbBtn,p,PhoneUiKit.BTN_SUBTLE,this);
            PhoneUiKit.styleButton(androidKbBtn,p,PhoneUiKit.BTN_SUBTLE,this);
            PhoneUiKit.styleButton(xiaomiBatBtn,p,PhoneUiKit.BTN_SUBTLE,this);

            PhoneUiKit.styleCard(trustCard,p,this);
            trustTitle.setTextColor(p.textSecondary);
            PhoneUiKit.styleSubcard(trustBox,p,this);
            trustSummary.setTextColor(p.emeraldText);
            autoStartSwitch.applyPalette(p);
            PhoneUiKit.styleButton(trustPcBtn,p,PhoneUiKit.BTN_TINT_EMERALD,this);
            PhoneUiKit.styleButton(checkUpdBtn,p,PhoneUiKit.BTN_SUBTLE,this);
            PhoneUiKit.styleButton(installUpdBtn,p,PhoneUiKit.BTN_TINT_ACCENT,this);
            PhoneUiKit.styleButton(leaveBtn,p,PhoneUiKit.BTN_SUBTLE,this);
            PhoneUiKit.styleButton(forgetBtn,p,PhoneUiKit.BTN_DANGER,this);

            PhoneUiKit.styleCard(appearCard,p,this);
            appearTitle.setTextColor(p.textSecondary);
            appearSub.setTextColor(p.textSecondary);
            themeSeg.applyPalette(p);
        });
    }

    private void buildBottomNav(){
        bottomNav=new LinearLayout(this);
        bottomNav.setOrientation(LinearLayout.HORIZONTAL);
        bottomNav.setPadding(dp(8),dp(6),dp(8),dp(6));
        String[] titles={"Home","Trackpad","Speed","Setup"};
        int[] icons={PhoneUiKit.TAB_HOME,PhoneUiKit.TAB_TRACKPAD,PhoneUiKit.TAB_SPEED,PhoneUiKit.TAB_SETUP};
        for(int i=0;i<TAB_COUNT;i++){
            final int tabIdx=i;
            LinearLayout item=new LinearLayout(this);
            item.setOrientation(LinearLayout.VERTICAL);
            item.setGravity(Gravity.CENTER);
            item.setPadding(dp(4),dp(6),dp(4),dp(6));
            item.setClickable(true);
            item.setFocusable(true);
            item.setOnClickListener(v->selectTab(tabIdx));

            PhoneUiKit.TabIconView icon=new PhoneUiKit.TabIconView(this,icons[i]);
            item.addView(icon,new LinearLayout.LayoutParams(dp(20),dp(20)));

            TextView label=new TextView(this);
            label.setText(titles[i]);
            label.setTextSize(11f);
            label.setGravity(Gravity.CENTER);
            label.setPadding(0,dp(3),0,0);
            item.addView(label);

            tabButtons[i]=item;
            tabIcons[i]=icon;
            tabLabels[i]=label;
            bottomNav.addView(item,new LinearLayout.LayoutParams(0,-2,1f));
        }

        onTheme(p->{
            bottomNav.setBackgroundColor(p.chromeBg);
            refreshTabBarStyles();
        });
    }

    private void applyWindowInsets(){
        rootLayout.setOnApplyWindowInsetsListener((v,windowInsets)->{
            WindowInsetsCompat compat=WindowInsetsCompat.toWindowInsetsCompat(windowInsets,v);
            Insets bars=compat.getInsets(WindowInsetsCompat.Type.systemBars()|WindowInsetsCompat.Type.displayCutout());
            Insets ime=compat.getInsets(WindowInsetsCompat.Type.ime());
            boolean imeVisible=ime.bottom>bars.bottom;
            rootLayout.setPadding(bars.left,0,bars.right,imeVisible ? ime.bottom : 0);
            appHeader.setPadding(dp(16),bars.top+dp(10),dp(16),dp(10));
            bottomNav.setPadding(dp(8),dp(6),dp(8),(imeVisible ? 0 : bars.bottom)+dp(6));
            return windowInsets;
        });
        rootLayout.requestApplyInsets();
    }

    private void selectTab(int index){
        if(index<0 || index>=TAB_COUNT)return;
        activeTab=index;
        for(int i=0;i<TAB_COUNT;i++){
            if(tabScrolls[i]!=null){
                tabScrolls[i].setVisibility(i==index ? View.VISIBLE : View.GONE);
            }
        }
        refreshTabBarStyles();
    }

    private void refreshTabBarStyles(){
        PhoneUiKit.Palette p=palette;
        for(int i=0;i<TAB_COUNT;i++){
            if(tabButtons[i]==null)continue;
            boolean active=i==activeTab;
            tabButtons[i].setBackground(active
                ? PhoneUiKit.roundedBox(p.accentSoftBg,Color.TRANSPARENT,dp(12),0)
                : PhoneUiKit.interactiveBg(Color.TRANSPARENT,p.subtleBtnBg,Color.TRANSPARENT,dp(12),0,p.rippleColor));
            tabIcons[i].setColor(active ? p.accentText : p.textSecondary);
            tabLabels[i].setTextColor(active ? p.accentText : p.textSecondary);
            tabLabels[i].setTypeface(null,active ? Typeface.BOLD : Typeface.NORMAL);
        }
    }

    private void applyTheme(int themeId){
        palette=PhoneUiKit.palette(themeId);
        rootLayout.setBackgroundColor(palette.chromeBg);
        tabFrame.setBackgroundColor(palette.canvasBg);
        headerDivider.setBackgroundColor(palette.chromeBorder);
        navDivider.setBackgroundColor(palette.chromeBorder);

        Window window=getWindow();
        if(window!=null){
            window.setStatusBarColor(palette.chromeBg);
            window.setNavigationBarColor(palette.chromeBg);
            WindowInsetsControllerCompat controller=WindowCompat.getInsetsController(window,rootLayout);
            if(controller!=null){
                controller.setAppearanceLightStatusBars(palette.lightBars);
                controller.setAppearanceLightNavigationBars(palette.lightBars);
            }
        }

        for(Consumer<PhoneUiKit.Palette> updater:themeUpdaters){
            updater.accept(palette);
        }
        refreshEnvironmentMembers();
        refreshPermissionState();
        refreshLiveLinkAndKeyboard();
    }

    private void onTheme(Consumer<PhoneUiKit.Palette> updater){
        themeUpdaters.add(updater);
    }

    private void addSegHeader(LinearLayout parent,String title,String hint,boolean topSpace){
        LinearLayout row=new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout.LayoutParams lp=new LinearLayout.LayoutParams(-1,-2);
        lp.topMargin=topSpace ? dp(12) : 0;
        lp.bottomMargin=dp(6);
        parent.addView(row,lp);

        TextView titleView=new TextView(this);
        titleView.setText(title);
        titleView.setTextSize(12.5f);
        titleView.setTypeface(null,Typeface.BOLD);
        row.addView(titleView,new LinearLayout.LayoutParams(0,-2,1f));

        TextView hintView=new TextView(this);
        hintView.setText(hint);
        hintView.setTextSize(11f);
        row.addView(hintView);

        onTheme(p->{
            titleView.setTextColor(p.textPrimary);
            hintView.setTextColor(p.textSecondary);
        });
    }

    private Button addPadActionButton(LinearLayout parent,String label,int style,boolean first,Runnable action){
        Button b=PhoneUiKit.createButton(this,label,palette,style);
        b.setTextSize(12f);
        b.setPadding(dp(4),dp(10),dp(4),dp(10));
        b.setOnClickListener(v->action.run());
        LinearLayout.LayoutParams lp=new LinearLayout.LayoutParams(0,-2,1f);
        lp.setMargins(first ? 0 : dp(3),0,dp(3),0);
        parent.addView(b,lp);
        return b;
    }

    private LinearLayout.LayoutParams cardParams(int topDp){
        LinearLayout.LayoutParams lp=new LinearLayout.LayoutParams(-1,-2);
        lp.topMargin=dp(topDp);
        return lp;
    }

    private LinearLayout.LayoutParams segParams(){
        return new LinearLayout.LayoutParams(-1,-2);
    }

    private String readVersionName(){
        try{
            String ver=getPackageManager().getPackageInfo(getPackageName(),0).versionName;
            return ver!=null && !ver.isEmpty() ? ver : "0.3.71";
        }catch(Exception ignored){
            return "0.3.71";
        }
    }

    private void setFeedback(String msg,boolean toast){
        if(status!=null)status.setText(msg);
        if(toast)showToast(msg);
    }

    private void showToast(String msg){
        if(activeToast!=null)activeToast.cancel();
        activeToast=Toast.makeText(this,msg,Toast.LENGTH_SHORT);
        activeToast.show();
    }

    @Override public void onConfigurationChanged(android.content.res.Configuration config){
        super.onConfigurationChanged(config);
        DeskTouchRuntime.publishPhoneGeometry();
    }

    private void updateVisibleScreenAwake(){
        // A connected, explicitly enabled phone must not fall asleep while
        // this setup/control screen is visible. Android owns this Activity
        // flag; system/third-party app power policies remain authoritative.
        boolean keep=DeskTouchRuntime.connectedCount()>0 &&
            (ShellInputClient.ready(this) || DeskTouchAccessibilityService.ready(this));
        if(keep)getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        else getWindow().clearFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
    }

    @Override protected void onResume(){
        super.onResume();
        updateVisibleScreenAwake();
        refreshPermissionState();
        refreshTrustSummary();
        refreshStreamOptionControls();
        refreshLiveLinkAndKeyboard();
        startLiveUiTicker();
        if(autoStartSwitch!=null)autoStartSwitch.setChecked(AutoConnectService.enabled(this));
        AutoConnectService.nudgeReconnect(this);
    }

    private void refreshStreamOptionControls(){
        if(mouseModeSeg!=null){
            mouseModeSeg.setSelectedIndex(PhoneUiKit.mouseModeToSegment(DeskTouchSession.readMouseMode(this)));
        }
        if(keyboardModeSeg!=null){
            keyboardModeSeg.setSelectedIndex(PhoneUiKit.keyboardModeToSegment(DeskTouchSession.readKeyboardMode(this)));
        }
        if(pointerFeelSeg!=null){
            pointerFeelSeg.setSelectedIndex(PhoneUiKit.pointerFeelToSegment(DeskTouchSession.readPointerFeelMode(this)));
        }
        if(dualPathControlsSwitch!=null){
            dualPathControlsSwitch.setChecked(DeskTouchSession.readDualPathControls(this));
        }
        if(keepChannelsWarmSwitch!=null){
            keepChannelsWarmSwitch.setChecked(DeskTouchSession.readKeepChannelsWarm(this));
        }
    }

    @Override protected void onPause(){
        stopLiveUiTicker();
        super.onPause();
    }

    private void startLiveUiTicker(){
        if(liveUiRunning)return;
        liveUiRunning=true;
        liveUiHandler.removeCallbacks(liveUiTick);
        liveUiHandler.postDelayed(liveUiTick,750L);
    }

    private void stopLiveUiTicker(){
        liveUiRunning=false;
        liveUiHandler.removeCallbacks(liveUiTick);
    }

    private void registerKeyboardDeviceListener(){
        inputManager=(android.hardware.input.InputManager)getSystemService(Context.INPUT_SERVICE);
        if(inputManager==null)return;
        inputDeviceListener=new android.hardware.input.InputManager.InputDeviceListener(){
            @Override public void onInputDeviceAdded(int deviceId){
                DeskTouchRuntime.refreshKeyboardBindings(MainActivity.this);
                runOnUiThread(()->{
                    if(!isFinishing()&&!isDestroyed())refreshLiveLinkAndKeyboard();
                });
            }
            @Override public void onInputDeviceRemoved(int deviceId){
                DeskTouchRuntime.refreshKeyboardBindings(MainActivity.this);
                runOnUiThread(()->{
                    if(!isFinishing()&&!isDestroyed())refreshLiveLinkAndKeyboard();
                });
            }
            @Override public void onInputDeviceChanged(int deviceId){
                DeskTouchRuntime.refreshKeyboardBindings(MainActivity.this);
                runOnUiThread(()->{
                    if(!isFinishing()&&!isDestroyed())refreshLiveLinkAndKeyboard();
                });
            }
        };
        inputManager.registerInputDeviceListener(inputDeviceListener,liveUiHandler);
    }

    private void refreshLiveLinkAndKeyboard(){
        DeskTouchSession.LiveStatusSnapshot snap=DeskTouchRuntime.liveSnapshot(this);
        DeskTouchSession active=DeskTouchRuntime.session(this);
        List<DeskTouchSession.EnvironmentTarget> targets=active.environmentTargets();
        String targetName=resolveTargetName(active,targets,snap);

        if(connectionSummary!=null){
            connectionSummary.setText(targets.isEmpty()
                ? "Linked Devices ("+DeskTouchRuntime.connectedCount()+" / 8)"
                : "Linked Devices ("+targets.size()+" / 8)");
        }
        if(headerSub!=null){
            if(snap!=null && snap.connected){
                int online=targets.isEmpty() ? Math.max(1,DeskTouchRuntime.connectedCount()) : targets.size();
                headerSub.setText(targetName+" • "+online+" online");
            }else{
                headerSub.setText("Not connected • Ready to scan");
            }
        }
        WifiLinkMonitor.LinkSnapshot local=(snap==null || !snap.connected)
            ? WifiLinkMonitor.inspect(this,false,0) : null;
        if(liveLinkView!=null){
            liveLinkView.setText(formatLiveLinkText(snap,local!=null ? local.describePlainLabel() : ""));
        }
        refreshTelemetryCards(snap,local);
        refreshControlButtons(active,snap,targetName);
        if(keyboardBindingsView!=null && snap!=null && snap.keyboardReport!=null){
            KeyboardBindingInspector.KeyboardReport kb=snap.keyboardReport;
            String pcTag=(kb.pcLayoutName!=null && !kb.pcLayoutName.isEmpty())
                ? " • PC layout: "+kb.pcLayoutName : "";
            int readyCount=0;
            for(KeyboardBindingInspector.ShortcutBinding b:kb.bindings)if(b.activeNow)readyCount++;
            keyboardBindingsView.setText("Keyboard: "+kb.connectedKeyboardName
                +" • Mode: "+kb.activeModeLabel
                +" • Ready bindings: "+readyCount+"/"+kb.bindings.size()+pcTag);
        }
        refreshTrustSummary();
    }

    private String resolveTargetName(DeskTouchSession active,
            List<DeskTouchSession.EnvironmentTarget> targets,
            DeskTouchSession.LiveStatusSnapshot snap){
        int selId=active!=null ? active.selectedEnvironmentTarget() : -1;
        if(targets!=null){
            for(DeskTouchSession.EnvironmentTarget t:targets){
                if(t.id==selId && t.name!=null && !t.name.isEmpty())return t.name;
            }
        }
        if(snap!=null && !snap.peerName.isEmpty())return snap.peerName;
        return "Windows PC";
    }

    private void refreshControlButtons(DeskTouchSession active,
            DeskTouchSession.LiveStatusSnapshot snap,String targetName){
        PhoneUiKit.Palette p=palette;
        boolean connected=snap!=null && snap.connected;
        boolean controlling=active!=null && active.hasClaim();
        if(takeControlBtn!=null){
            if(controlling){
                takeControlBtn.setText("Controlling "+targetName+" ●");
                PhoneUiKit.styleButton(takeControlBtn,p,PhoneUiKit.BTN_ACTIVE_GREEN,this);
            }else{
                takeControlBtn.setText("Control PC Screen");
                PhoneUiKit.styleButton(takeControlBtn,p,PhoneUiKit.BTN_PRIMARY,this);
            }
        }
        if(trackpadTargetTitle!=null){
            trackpadTargetTitle.setText(connected
                ? (controlling ? "Controlling "+targetName : "Target: "+targetName)
                : "Touchpad (Not Connected)");
        }
    }

    private void refreshTelemetryCards(DeskTouchSession.LiveStatusSnapshot snap,
            WifiLinkMonitor.LinkSnapshot local){
        PhoneUiKit.Palette p=palette;
        if(snap==null || !snap.connected){
            setBadgeState(p.neutralBadgeBg,p.textSecondary,"Offline");
            if(mouseTag!=null){mouseTag.setText("Offline");mouseTag.setTextColor(p.textMuted);}
            if(mouseMetric!=null)mouseMetric.setText("Tap Find Devices");
            if(kbTag!=null){kbTag.setText("Offline");kbTag.setTextColor(p.textMuted);}
            if(kbMetric!=null)kbMetric.setText("Auto-reconnect ready");
            if(speedWifiRtt!=null)speedWifiRtt.setText("—");
            if(speedWifiSub!=null)speedWifiSub.setText(local!=null ? local.describePlainLabel() : "Waiting for link");
            if(wifiLossBadge!=null){
                wifiLossBadge.setText("Offline");
                PhoneUiKit.stylePill(wifiLossBadge,p.neutralBadgeBg,Color.TRANSPARENT,p.neutralBadgeText,this);
            }
            if(speedMobRtt!=null)speedMobRtt.setText("—");
            if(speedMobSub!=null)speedMobSub.setText("Ready alongside Wi-Fi");
            if(mobLossBadge!=null){
                mobLossBadge.setText("Standby");
                PhoneUiKit.stylePill(mobLossBadge,p.neutralBadgeBg,Color.TRANSPARENT,p.neutralBadgeText,this);
            }
            return;
        }

        if(snap.cable){
            setBadgeState(p.emeraldSoftBg,p.emeraldText,"Data Cable Active");
            if(mouseTag!=null){mouseTag.setText("Cable");mouseTag.setTextColor(p.emeraldText);}
            if(mouseMetric!=null)mouseMetric.setText("< 1 ms • 0% loss");
            if(kbTag!=null){kbTag.setText("Cable");kbTag.setTextColor(p.emeraldText);}
            if(kbMetric!=null)kbMetric.setText("< 1 ms • Instant");
            if(speedWifiRtt!=null)speedWifiRtt.setText("Cable");
            if(speedWifiSub!=null)speedWifiSub.setText("USB direct link");
            if(wifiLossBadge!=null){
                wifiLossBadge.setText("0% loss");
                PhoneUiKit.stylePill(wifiLossBadge,p.emeraldSoftBg,Color.TRANSPARENT,p.emeraldText,this);
            }
            if(speedMobRtt!=null)speedMobRtt.setText("Standby");
            if(speedMobSub!=null)speedMobSub.setText("Cable has priority");
            if(mobLossBadge!=null){
                mobLossBadge.setText("Idle");
                PhoneUiKit.stylePill(mobLossBadge,p.neutralBadgeBg,Color.TRANSPARENT,p.neutralBadgeText,this);
            }
            return;
        }

        boolean dualActive=snap.mobConfirmed && (snap.preferMobileMouse || snap.preferMobileKeyboard);
        if(dualActive){
            setBadgeState(p.emeraldSoftBg,p.emeraldText,"Dual-Path Connected");
        }else if(WifiLinkMonitor.BAND_CELLULAR.equals(snap.band)){
            setBadgeState(p.emeraldSoftBg,p.emeraldText,"Mobile Connected");
        }else{
            setBadgeState(p.accentSoftBg,p.accentText,"Wireless Active");
        }

        double wifiMs=snap.wifiRttMs>0.0 ? snap.wifiRttMs : snap.rttMs;
        double mobMs=snap.mobRttMs;
        boolean mMob=snap.preferMobileMouse && snap.mobConfirmed;
        boolean kMob=snap.preferMobileKeyboard && snap.mobConfirmed;

        if(mouseTag!=null){
            mouseTag.setText(mMob ? "Mobile" : "Wireless");
            mouseTag.setTextColor(mMob ? p.emeraldText : p.accentText);
        }
        if(mouseMetric!=null){
            double mRtt=mMob ? mobMs : wifiMs;
            double mLoss=mMob ? snap.mobLossPermille : snap.wifiLossPermille;
            mouseMetric.setText((mRtt>0.0 ? formatMs(mRtt) : "Measuring...")+" • "+formatLossPct(mLoss));
        }

        if(kbTag!=null){
            kbTag.setText(kMob ? "Mobile" : "Wireless");
            kbTag.setTextColor(kMob ? p.emeraldText : p.accentText);
        }
        if(kbMetric!=null){
            double kRtt=kMob ? mobMs : wifiMs;
            kbMetric.setText((kRtt>0.0 ? formatMs(kRtt) : "Measuring...")
                +(snap.dualPathControls ? " • Dual-path" : " • Single"));
        }

        if(speedWifiRtt!=null)speedWifiRtt.setText(wifiMs>0.0 ? formatMs(wifiMs) : "Measuring...");
        if(speedWifiSub!=null){
            String bandText=formatBandPlain(snap.band,snap.speedMbps);
            String jitterText=snap.jitterMs>=0.2
                ? String.format(Locale.US," • jitter %.1f ms",snap.jitterMs) : "";
            speedWifiSub.setText(bandText+jitterText);
        }
        if(wifiLossBadge!=null){
            boolean clean=snap.wifiLossPermille<10.0;
            wifiLossBadge.setText(formatLossPct(snap.wifiLossPermille));
            PhoneUiKit.stylePill(wifiLossBadge,
                clean ? p.emeraldSoftBg : p.amberSoftBg,
                Color.TRANSPARENT,
                clean ? p.emeraldText : p.amberText,this);
        }

        if(snap.mobConfirmed){
            if(speedMobRtt!=null)speedMobRtt.setText(mobMs>0.0 ? formatMs(mobMs) : "Active");
            if(speedMobSub!=null)speedMobSub.setText("Ready alongside Wi-Fi");
            if(mobLossBadge!=null){
                boolean clean=snap.mobLossPermille<10.0;
                mobLossBadge.setText(formatLossPct(snap.mobLossPermille));
                PhoneUiKit.stylePill(mobLossBadge,
                    clean ? p.emeraldSoftBg : p.amberSoftBg,
                    Color.TRANSPARENT,
                    clean ? p.emeraldText : p.amberText,this);
            }
        }else{
            if(speedMobRtt!=null)speedMobRtt.setText("Standby");
            if(speedMobSub!=null)speedMobSub.setText("Engages automatically if needed");
            if(mobLossBadge!=null){
                mobLossBadge.setText("Ready");
                PhoneUiKit.stylePill(mobLossBadge,p.neutralBadgeBg,Color.TRANSPARENT,p.neutralBadgeText,this);
            }
        }
    }

    private void setBadgeState(int bg,int fg,String text){
        if(liveBadge==null)return;
        liveBadge.setBackground(PhoneUiKit.roundedBox(bg,Color.TRANSPARENT,dp(999),0));
        if(liveDot!=null)liveDot.setBackground(PhoneUiKit.roundedBox(fg,Color.TRANSPARENT,dp(999),0));
        if(liveBadgeText!=null){
            liveBadgeText.setText(text);
            liveBadgeText.setTextColor(fg);
        }
    }

    private static String formatMs(double ms){
        return String.format(Locale.US,"%.1f ms",ms);
    }

    private static String formatLossPct(double lossPermille){
        if(lossPermille<1.0)return "0% loss";
        return String.format(Locale.US,"%.1f%% loss",lossPermille/10.0);
    }

    static String formatLiveLinkText(DeskTouchSession.LiveStatusSnapshot snap, String offlineNetworkLabel){
        if(snap==null){
            return "Live link: waiting for connection";
        }
        if(!snap.connected){
            return "Live link: Disconnected • Phone network: "+offlineNetworkLabel;
        }
        if(snap.cable){
            return "Live link: "+snap.peerName+" via Data cable";
        }
        String path=formatBandPlain(snap.band,snap.speedMbps);
        String ping=snap.rttMs>0.0
            ? String.format(java.util.Locale.US,"ping %.1f ms",snap.rttMs)
            : "measuring ping...";
        String extra="";
        if(snap.jitterMs>=0.2){
            extra+=String.format(java.util.Locale.US," • jitter %.1f ms",snap.jitterMs);
        }
        if(snap.lossPermille>=1.0){
            extra+=String.format(java.util.Locale.US," • loss %.1f%%",snap.lossPermille/10.0);
        }
        String base="Live link: "+snap.peerName+" via "+path+" • "+ping+extra;
        if(snap.mobConfirmed || snap.preferMobileMouse || snap.preferMobileKeyboard){
            String mousePath=snap.preferMobileMouse
                ? ("Mobile"+(snap.mobRttMs>0.0 ? String.format(java.util.Locale.US," (%.1f ms)",snap.mobRttMs) : ""))
                : ("Wireless"+(snap.wifiRttMs>0.0 ? String.format(java.util.Locale.US," (%.1f ms)",snap.wifiRttMs) : ""));
            String kbPath=snap.preferMobileKeyboard
                ? ("Mobile"+(snap.mobRttMs>0.0 ? String.format(java.util.Locale.US," (%.1f ms)",snap.mobRttMs) : ""))
                : ("Wireless"+(snap.wifiRttMs>0.0 ? String.format(java.util.Locale.US," (%.1f ms)",snap.wifiRttMs) : ""));
            String dualTag=snap.dualPathControls ? " [Dual-path]" : "";
            return base+"\nMouse: "+mousePath+" • Keys: "+kbPath+dualTag;
        }
        return base;
    }

    private static String formatBandPlain(String band, int speedMbps){
        String base;
        if(WifiLinkMonitor.BAND_24_GHZ.equals(band)) base="2.4 GHz wireless";
        else if(WifiLinkMonitor.BAND_5_GHZ.equals(band)) base="5 GHz wireless";
        else if(WifiLinkMonitor.BAND_6_GHZ.equals(band)) base="6 GHz wireless";
        else if(WifiLinkMonitor.BAND_CELLULAR.equals(band)) base="Mobile network";
        else if("ethernet".equals(band)) base="Wired network";
        else base="Wireless";
        return speedMbps>0 ? base+" ("+speedMbps+" Mbps)" : base;
    }

    private void showKeyboardShortcutsDialog(){
        DeskTouchSession.LiveStatusSnapshot snap=DeskTouchRuntime.liveSnapshot(this);
        KeyboardBindingInspector.KeyboardReport kb=snap!=null ? snap.keyboardReport : null;
        if(kb==null){
            kb=KeyboardBindingInspector.inspect(
                this,ShellInputClient.ready(this),DeskTouchAccessibilityService.ready(this),"");
        }
        new AlertDialog.Builder(this)
            .setTitle("Connected keyboard & supported bindings")
            .setMessage(kb.formatSummaryLines())
            .setPositiveButton("Close",null)
            .show();
    }

    private void refreshTrustSummary(){
        if(trustSummary==null)return;
        int count=new TrustedWindows(this).fingerprints().size();
        String connectedFp=DeskTouchRuntime.session(this).connectedFingerprint();
        boolean currentTrusted=connectedFp!=null &&
            new TrustedWindows(this).get(connectedFp)!=null;
        String base=count==0
            ?"Trusted computers saved: 0 (auto-reconnect disabled until approved)"
            :"Trusted computers saved: "+count;
        if(connectedFp!=null)
            base+=currentTrusted
                ?" • Current computer is trusted"
                :" • Current computer not trusted yet";
        trustSummary.setText(base);
        if(trustBadgeBtn!=null){
            if(connectedFp==null){
                trustBadgeBtn.setText("Unpaired");
                PhoneUiKit.styleButton(trustBadgeBtn,palette,PhoneUiKit.BTN_SUBTLE,this);
            }else if(currentTrusted){
                trustBadgeBtn.setText("Trusted ✓");
                PhoneUiKit.styleButton(trustBadgeBtn,palette,PhoneUiKit.BTN_TINT_EMERALD,this);
            }else{
                trustBadgeBtn.setText("Trust PC");
                PhoneUiKit.styleButton(trustBadgeBtn,palette,PhoneUiKit.BTN_TINT_AMBER,this);
            }
        }
        if(autoStartSwitch!=null)autoStartSwitch.setChecked(AutoConnectService.enabled(this));
    }

    private static void sendRemoteTextWithNewlines(DeskTouchSession target,String added){
        int start=0;
        for(int i=0;i<added.length();i++){
            if(added.charAt(i)=='\n'){
                if(i>start)target.text(added.substring(start,i));
                target.key(0x0D,true);target.key(0x0D,false);
                start=i+1;
            }
        }
        if(start<added.length())target.text(added.substring(start));
    }

    private void refreshPermissionState(){
        boolean sysEnabled=PermissionContinuity.systemAccessibilityEnabled(this);
        boolean shellReady=ShellInputClient.ready(this);
        if(allowWindowsSwitch!=null)
            allowWindowsSwitch.setChecked(DeskTouchAccessibilityService.allowed(this));
        if(allowShellBridgeSwitch!=null)
            allowShellBridgeSwitch.setChecked(ShellInputClient.allowed(this));
        if(autoPausePaymentsSwitch!=null)
            autoPausePaymentsSwitch.setChecked(
                DeskTouchAccessibilityService.autoPausePaymentAppsEnabled(this));
        if(autoPauseDisconnectSwitch!=null)
            autoPauseDisconnectSwitch.setChecked(
                DeskTouchAccessibilityService.autoPauseOnDisconnectEnabled(this));

        if(pauseBankingButton!=null){
            pauseBankingButton.setText(sysEnabled ? "Pay Mode" : "Pay Safe: ON");
            PhoneUiKit.styleButton(pauseBankingButton,palette,
                sysEnabled ? PhoneUiKit.BTN_TINT_EMERALD : PhoneUiKit.BTN_TINT_AMBER,this);
        }

        if(safetyBadge!=null){
            if(shellReady){
                safetyBadge.setText("Bridge Active");
                PhoneUiKit.stylePill(safetyBadge,palette.emeraldSoftBg,Color.TRANSPARENT,palette.emeraldText,this);
            }else if(sysEnabled){
                safetyBadge.setText("Ready");
                PhoneUiKit.stylePill(safetyBadge,palette.emeraldSoftBg,Color.TRANSPARENT,palette.emeraldText,this);
            }else if(DeskTouchAccessibilityService.isPausedForBanking(this)){
                safetyBadge.setText("Pay Safe");
                PhoneUiKit.stylePill(safetyBadge,palette.amberSoftBg,Color.TRANSPARENT,palette.amberText,this);
            }else{
                safetyBadge.setText("Off");
                PhoneUiKit.stylePill(safetyBadge,palette.neutralBadgeBg,Color.TRANSPARENT,palette.neutralBadgeText,this);
            }
        }

        if(protectionDesc!=null){
            if(shellReady){
                protectionDesc.setText("Virtual Bridge Active (Accessibility Not Needed)");
            }else if(!sysEnabled){
                protectionDesc.setText(DeskTouchAccessibilityService.isPausedForBanking(this)
                    ? "Accessibility Paused for Payment Apps"
                    : "Accessibility Off • Tap Pay Safe or Setup to enable");
            }else{
                protectionDesc.setText(ShellInputClient.allowed(this)
                    ? "Accessibility Active • Virtual Bridge Ready"
                    : "Accessibility Active");
            }
        }

        if(permissionState==null)return;
        String bridgeLine=shellReady
            ? "active (works with Accessibility OFF)"
            : (ShellInputClient.allowed(this)
                ? "standby (starts automatically when cable or Wireless Debugging is connected to Windows)"
                : "disabled in DeskTouch");
        permissionState.setText(PermissionContinuity.state(this) +
            "\nVirtual Bridge: " + bridgeLine +
            "\nDeskTouch keyboard: " +
            (DeskTouchInputMethod.isSelected(this)
                ? "selected for focused-field typing."
                : "not selected (optional when Accessibility is used)."));
    }

    private int dp(int size){return Math.round(getResources().getDisplayMetrics().density*size);}

    private void refreshEnvironmentMembers(){
        if(environmentList==null)return;
        environmentList.removeAllViews();
        DeskTouchSession active=DeskTouchRuntime.session(this);
        List<DeskTouchSession.EnvironmentTarget> targets=active.environmentTargets();
        if(targets.isEmpty()){
            if(connectionSummary!=null){
                connectionSummary.setText(active.isConnected()
                    ?"Waiting for authenticated environment membership..."
                    :"Linked Devices (0 / 8)");
            }
            TextView empty=new TextView(this);
            empty.setText(active.isConnected()
                ?"Synchronizing environment devices..."
                :"Tap Find Devices above to connect to a Windows hub on your Wi-Fi.");
            empty.setTextSize(12f);
            empty.setTextColor(palette.textSecondary);
            empty.setPadding(dp(2),dp(4),dp(2),dp(4));
            environmentList.addView(empty);
            return;
        }
        if(connectionSummary!=null){
            connectionSummary.setText("One DeskTouch environment • "+targets.size()+" device(s) online");
        }
        for(int i=0;i<targets.size();i++){
            DeskTouchSession.EnvironmentTarget device=targets.get(i);
            boolean isSelf=device.id==active.ownEnvironmentId();
            boolean selectable=!isSelf && device.supportsInput;
            boolean selected=device.id==active.selectedEnvironmentTarget();

            LinearLayout row=new LinearLayout(this);
            row.setOrientation(LinearLayout.HORIZONTAL);
            row.setGravity(Gravity.CENTER_VERTICAL);
            row.setPadding(dp(12),dp(10),dp(12),dp(10));
            if(selected && selectable){
                row.setBackground(PhoneUiKit.interactiveBg(
                    palette.accentSoftBg,palette.accentSoftBg,palette.accentBorder,
                    dp(12),Math.max(1,dp(1)),palette.rippleColor));
            }else{
                row.setBackground(selectable
                    ? PhoneUiKit.interactiveBg(palette.subcardBg,palette.subtleBtnBg,
                        palette.border,dp(12),Math.max(1,dp(1)),palette.rippleColor)
                    : PhoneUiKit.roundedBox(palette.subcardBg,palette.border,
                        dp(12),Math.max(1,dp(1))));
            }

            LinearLayout textCol=new LinearLayout(this);
            textCol.setOrientation(LinearLayout.VERTICAL);

            TextView nameView=new TextView(this);
            nameView.setText(device.name+(selected && selectable ? "  ● Active" : ""));
            nameView.setTextSize(13f);
            nameView.setTypeface(null,Typeface.BOLD);
            nameView.setTextColor(palette.textPrimary);
            textCol.addView(nameView);

            TextView roleView=new TextView(this);
            roleView.setText(isSelf ? device.platform+" • This phone" : device.platform);
            roleView.setTextSize(11.5f);
            roleView.setTextColor(palette.textSecondary);
            roleView.setPadding(0,dp(1),0,0);
            textCol.addView(roleView);

            LinearLayout.LayoutParams colLp=new LinearLayout.LayoutParams(0,-2,1f);
            colLp.rightMargin=dp(8);
            row.addView(textCol,colLp);

            TextView actionTag=new TextView(this);
            if(isSelf){
                actionTag.setText("Local");
                actionTag.setTextColor(palette.textMuted);
            }else if(!device.supportsInput){
                actionTag.setText("Permission Req.");
                actionTag.setTextColor(palette.amberText);
            }else if(selected){
                actionTag.setText("Selected");
                actionTag.setTextColor(palette.accentText);
            }else{
                actionTag.setText("Switch");
                actionTag.setTextColor(palette.accentText);
            }
            actionTag.setTextSize(11.5f);
            actionTag.setTypeface(null,Typeface.BOLD);
            row.addView(actionTag);

            if(selectable){
                row.setClickable(true);
                row.setFocusable(true);
                row.setOnClickListener(v->{
                    active.selectEnvironmentTarget(device.id);
                    active.claimControl();
                    refreshEnvironmentMembers();
                    refreshLiveLinkAndKeyboard();
                });
            }

            LinearLayout.LayoutParams rowLp=new LinearLayout.LayoutParams(-1,-2);
            if(i>0)rowLp.topMargin=dp(8);
            environmentList.addView(row,rowLp);
        }
    }

    private void scan(){
        if(scanning)return;
        scanning=true;
        status.setText("Finding a DeskTouch environment hub on your private LAN...");
        dynamicDeviceThemeUpdaters.clear();
        deviceList.removeAllViews();
        new Thread(()->{
            try{
                List<DesktopDiscovery.Device> list=DesktopDiscovery.scan();
                runOnUiThread(()->{
                    if(isFinishing()||isDestroyed())return;
                    java.util.LinkedHashMap<String,List<DesktopDiscovery.Device>> groups=
                        DesktopDiscovery.windowsGroups(list);
                    java.util.Set<String> connected=new java.util.HashSet<>();
                    for(DesktopDiscovery.Device member:list)
                        if(!member.android&&DeskTouchRuntime.isConnected(member.fingerprint))
                            connected.add(member.fingerprint);
                    java.util.Set<String> approved=new java.util.HashSet<>();
                    try {approved.addAll(new TrustedWindows(this).fingerprints());}
                    catch(Exception ignored){}
                    if(groups.isEmpty())
                        status.setText("No DeskTouch environment found. Start DeskTouch on a Windows hub.");
                    else status.setText("Choose one environment. Connected members will "+
                        "appear automatically after joining the hub.");
                    for(List<DesktopDiscovery.Device> members:groups.values()){
                        DesktopDiscovery.Device gateway=DesktopDiscovery.chooseGateway(
                            members,connected,approved);
                        if(gateway==null)continue;
                        String names=String.join(" + ",members.stream()
                            .map(m->m.name).toArray(String[]::new));
                        boolean isJoined=DeskTouchRuntime.isConnected(gateway.fingerprint);

                        LinearLayout hubRow=new LinearLayout(this);
                        hubRow.setOrientation(LinearLayout.HORIZONTAL);
                        hubRow.setGravity(Gravity.CENTER_VERTICAL);
                        hubRow.setPadding(dp(12),dp(10),dp(12),dp(10));
                        hubRow.setClickable(true);
                        hubRow.setFocusable(true);

                        TextView hubTitle=new TextView(this);
                        hubTitle.setText("DeskTouch hub • "+names);
                        hubTitle.setTextSize(12.5f);
                        hubTitle.setTypeface(null,Typeface.BOLD);
                        LinearLayout.LayoutParams htLp=new LinearLayout.LayoutParams(0,-2,1f);
                        htLp.rightMargin=dp(8);
                        hubRow.addView(hubTitle,htLp);

                        TextView hubTag=new TextView(this);
                        hubTag.setText(isJoined ? "Joined" : "Join");
                        hubTag.setTextSize(11f);
                        hubTag.setTypeface(null,Typeface.BOLD);
                        hubTag.setPadding(dp(8),dp(3),dp(8),dp(3));
                        hubRow.addView(hubTag);

                        Runnable styleHubRow=()->{
                            hubRow.setBackground(PhoneUiKit.interactiveBg(
                                palette.subcardBg,palette.subtleBtnBg,palette.border,
                                dp(12),Math.max(1,dp(1)),palette.rippleColor));
                            hubTitle.setTextColor(palette.textPrimary);
                            PhoneUiKit.stylePill(hubTag,
                                isJoined ? palette.emeraldSoftBg : palette.accentSoftBg,
                                Color.TRANSPARENT,
                                isJoined ? palette.emeraldText : palette.accentText,this);
                        };
                        styleHubRow.run();
                        dynamicDeviceThemeUpdaters.add(styleHubRow);

                        hubRow.setOnClickListener(v->{
                            if(DeskTouchRuntime.isConnected(gateway.fingerprint)){
                                session=DeskTouchRuntime.select(this,gateway.fingerprint);
                                refreshEnvironmentMembers();return;
                            }
                            if(DeskTouchRuntime.connectedCount()>0){
                                status.setText("Already joined a DeskTouch environment. "+
                                    "Leave it before switching hubs.");return;
                            }
                            if(!gateway.ready){
                                status.setText("Environment gateway is at capacity or unavailable.");
                                return;
                            }
                            new AlertDialog.Builder(this)
                                .setTitle("Join DeskTouch environment")
                                .setMessage("Connect once to "+gateway.name+
                                    ". Other Windows PCs and Android phones attached "+
                                    "to that hub will appear inside the same environment. "+
                                    "Each device joins through its own approved TLS "+
                                    "connection to the hub; this phone does not need to "+
                                    "pair separately with every member.")
                                .setNegativeButton("Cancel",null)
                                .setPositiveButton("Join environment",(dialog,which)->{
                                    android.graphics.Point size=PhoneScreenGeometry.read(this);
                                    AutoConnectService.setReconnect(this,true);
                                    AutoConnectService.resumePeer(this,gateway.fingerprint);
                                    session=DeskTouchRuntime.select(this,gateway.fingerprint);
                                    session.connect(gateway,size.x,size.y,true,null);
                                    status.setText("Connecting to shared environment through "+
                                        gateway.name+". Approve pairing on that Windows PC.");
                                }).show();
                        });

                        LinearLayout.LayoutParams rowLp=new LinearLayout.LayoutParams(-1,-2);
                        rowLp.topMargin=dp(6);
                        deviceList.addView(hubRow,rowLp);
                    }
                });
            }catch(Exception e){
                runOnUiThread(()->{
                    if(!isFinishing()&&!isDestroyed())
                        status.setText("Environment discovery failed: "+e.getMessage());
                });
            }finally{scanning=false;}
        },"DeskTouch LAN environment discovery").start();
    }

    @Override public boolean dispatchKeyEvent(KeyEvent e){
        // The Android IME EditText handles composed Unicode text through
        // TextWatcher. Hardware keyboards control Windows when the touchpad
        // has focus, without stealing typing from the IME.
        if(session!=null && DeskTouchRuntime.session(MainActivity.this).isActive() &&
            !(getCurrentFocus() instanceof EditText) &&
            (e.getSource()&InputDevice.SOURCE_KEYBOARD)==InputDevice.SOURCE_KEYBOARD){
            int vk=toWindowsKey(e.getKeyCode());
            if(vk!=0 && (e.getAction()==KeyEvent.ACTION_DOWN ||
                e.getAction()==KeyEvent.ACTION_UP)){
                if(e.getRepeatCount()==0)DeskTouchRuntime.session(MainActivity.this).key(vk,
                    e.getAction()==KeyEvent.ACTION_DOWN);
                return true;
            }
        }
        return super.dispatchKeyEvent(e);
    }

    static int toWindowsKey(int c){
        if(c>=KeyEvent.KEYCODE_A&&c<=KeyEvent.KEYCODE_Z)
            return 0x41+c-KeyEvent.KEYCODE_A;
        if(c>=KeyEvent.KEYCODE_0&&c<=KeyEvent.KEYCODE_9)
            return 0x30+c-KeyEvent.KEYCODE_0;
        if(c>=KeyEvent.KEYCODE_F1&&c<=KeyEvent.KEYCODE_F12)
            return 0x70+c-KeyEvent.KEYCODE_F1;
        switch(c){
            case KeyEvent.KEYCODE_ENTER:return 0x0D;
            case KeyEvent.KEYCODE_DEL:return 0x08;
            case KeyEvent.KEYCODE_FORWARD_DEL:return 0x2E;
            case KeyEvent.KEYCODE_INSERT:return 0x2D;
            case KeyEvent.KEYCODE_MOVE_HOME:return 0x24;
            case KeyEvent.KEYCODE_MOVE_END:return 0x23;
            case KeyEvent.KEYCODE_PAGE_UP:return 0x21;
            case KeyEvent.KEYCODE_PAGE_DOWN:return 0x22;
            case KeyEvent.KEYCODE_TAB:return 0x09;
            case KeyEvent.KEYCODE_ESCAPE:return 0x1B;
            case KeyEvent.KEYCODE_SPACE:return 0x20;
            case KeyEvent.KEYCODE_DPAD_LEFT:return 0x25;
            case KeyEvent.KEYCODE_DPAD_UP:return 0x26;
            case KeyEvent.KEYCODE_DPAD_RIGHT:return 0x27;
            case KeyEvent.KEYCODE_DPAD_DOWN:return 0x28;
            case KeyEvent.KEYCODE_SHIFT_LEFT:return 0xA0;
            case KeyEvent.KEYCODE_SHIFT_RIGHT:return 0xA1;
            case KeyEvent.KEYCODE_CTRL_LEFT:return 0xA2;
            case KeyEvent.KEYCODE_CTRL_RIGHT:return 0xA3;
            case KeyEvent.KEYCODE_ALT_LEFT:return 0xA4;
            case KeyEvent.KEYCODE_ALT_RIGHT:return 0xA5;
            case KeyEvent.KEYCODE_META_LEFT:return 0x5B;
            case KeyEvent.KEYCODE_META_RIGHT:return 0x5C;
            case KeyEvent.KEYCODE_MENU:return 0x5D;
            case KeyEvent.KEYCODE_CAPS_LOCK:return 0x14;
            case KeyEvent.KEYCODE_NUM_LOCK:return 0x90;
            case KeyEvent.KEYCODE_SCROLL_LOCK:return 0x91;
            case KeyEvent.KEYCODE_SYSRQ:return 0x2C;
            case KeyEvent.KEYCODE_BREAK:return 0x13;
            case KeyEvent.KEYCODE_MINUS:return 0xBD;
            case KeyEvent.KEYCODE_EQUALS:return 0xBB;
            case KeyEvent.KEYCODE_LEFT_BRACKET:return 0xDB;
            case KeyEvent.KEYCODE_RIGHT_BRACKET:return 0xDD;
            case KeyEvent.KEYCODE_BACKSLASH:return 0xDC;
            case KeyEvent.KEYCODE_SEMICOLON:return 0xBA;
            case KeyEvent.KEYCODE_APOSTROPHE:return 0xDE;
            case KeyEvent.KEYCODE_COMMA:return 0xBC;
            case KeyEvent.KEYCODE_PERIOD:return 0xBE;
            case KeyEvent.KEYCODE_SLASH:return 0xBF;
            case KeyEvent.KEYCODE_GRAVE:return 0xC0;
            default:return 0;
        }
    }

    @Override public void onWindowFocusChanged(boolean focus){
        super.onWindowFocusChanged(focus);
        // Opening an Android soft keyboard may temporarily change window
        // focus. It must not silently revoke the approved Windows input
        // lease before IME text is committed. Explicit Return / Disconnect
        // and physical Windows input still revoke that lease.
    }

    @Override protected void onDestroy(){
        stopLiveUiTicker();
        if(inputManager!=null && inputDeviceListener!=null){
            try{inputManager.unregisterInputDeviceListener(inputDeviceListener);}
            catch(RuntimeException ignored){}
            inputDeviceListener=null;
        }
        DeskTouchRuntime.setLiveStatusListener(null);
        DeskTouchRuntime.setListener(null);
        // The foreground reconnect service owns the authenticated TLS sessions.
        // Closing this setup screen must not disconnect the selected phone.
        super.onDestroy();
    }
}
