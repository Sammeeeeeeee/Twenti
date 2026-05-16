using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace Twenti.Services;

/// <summary>
/// Attached property that gives any FrameworkElement a subtle scale-on-hover
/// effect — same trick the WinUI 3 Gallery's XamlCompInterop sample uses to
/// give buttons a "lift" when the pointer enters.
///
/// Usage in XAML:
/// <code>
///   xmlns:svc="using:Twenti.Services"
///   &lt;Button svc:HoverScale.Enabled="True" .../&gt;
/// </code>
///
/// Animations run on the Composition layer (GPU), so they don't go through
/// the XAML layout pass and stay smooth even while the timer is ticking.
/// </summary>
public static class HoverScale
{
    private const float HoverScaleAmount = 1.04f;
    private const float PressedScaleAmount = 0.97f;
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(140);

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(HoverScale),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if ((bool)e.NewValue) Attach(el);
        else Detach(el);
    }

    private static void Attach(FrameworkElement el)
    {
        el.PointerEntered += OnPointerEntered;
        el.PointerExited  += OnPointerExited;
        el.PointerPressed += OnPointerPressed;
        el.PointerReleased += OnPointerReleased;
        el.PointerCanceled += OnPointerExited;
        el.PointerCaptureLost += OnPointerExited;
        el.SizeChanged += OnSizeChanged;
        el.Loaded += OnLoaded;
    }

    private static void Detach(FrameworkElement el)
    {
        el.PointerEntered -= OnPointerEntered;
        el.PointerExited  -= OnPointerExited;
        el.PointerPressed -= OnPointerPressed;
        el.PointerReleased -= OnPointerReleased;
        el.PointerCanceled -= OnPointerExited;
        el.PointerCaptureLost -= OnPointerExited;
        el.SizeChanged -= OnSizeChanged;
        el.Loaded -= OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el) UpdateCenterPoint(el);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement el) UpdateCenterPoint(el);
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement el) AnimateScale(el, HoverScaleAmount);
    }

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement el) AnimateScale(el, 1.0f);
    }

    private static void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement el) AnimateScale(el, PressedScaleAmount);
    }

    private static void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // After release the pointer is usually still over the element, so
        // settle back to the hover scale rather than 1.0.
        if (sender is FrameworkElement el) AnimateScale(el, HoverScaleAmount);
    }

    private static void UpdateCenterPoint(FrameworkElement el)
    {
        var visual = ElementCompositionPreview.GetElementVisual(el);
        visual.CenterPoint = new Vector3(
            (float)(el.ActualWidth / 2),
            (float)(el.ActualHeight / 2),
            0);
    }

    private static void AnimateScale(FrameworkElement el, float target)
    {
        UpdateCenterPoint(el);
        var visual = ElementCompositionPreview.GetElementVisual(el);
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0.9f),
            new Vector2(0.2f, 1.0f));
        var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(1f, new Vector3(target, target, 1f), ease);
        anim.Duration = AnimationDuration;
        visual.StartAnimation("Scale", anim);
    }
}
