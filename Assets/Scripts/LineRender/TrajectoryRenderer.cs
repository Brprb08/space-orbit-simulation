using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using TMPro;

/** 
* Handles the rendering of trajectory prediction lines for celestial bodies.
* This includes prediction lines, origin lines, and apogee/perigee indicators.
* The class also updates the UI elements for apogee and perigee distances
* and toggles line visibility based on user inputs and simulation state.
**/
[RequireComponent(typeof(LineRenderer))]
public class TrajectoryRenderer : MonoBehaviour
{
    [Header("Trajectory Prediction Settings")]
    public int predictionSteps = 5000;
    public float predictionDeltaTime = 5f;
    public bool orbitIsDirty = true;
    private bool isThrusting = false;
    private bool update = false;

    [Header("References")]
    public TextMeshProUGUI apogeeText;
    public TextMeshProUGUI perigeeText;
    public ThrustController thrustController;
    [SerializeField]
    public CameraMovement cameraMovement;
    private Camera mainCamera;
    private NBody trackedBody;

    private UIManager uIManager;

    [Header("Line Display Flags")]
    private bool showPredictionLines;
    private bool showOriginLines;
    private bool showApogeePerigeeLines;

    [Header("Coroutine")]
    private Coroutine predictionCoroutine;
    private float updateIntervalApogeePerigee = 10f;
    private float apogeePerigeeUpdateTime = 1f;

    public float apogeeDistance = 0f;
    public float perigeeDistance = 0f;
    public bool justSwitchedTrack = false;

    [Header("Optimizations")]
    public bool useLOD = true;
    public float lodDistanceThreshold = 5000f;
    public float maxRecomputeInterval = 5f;

    [Header("Procedural Lines")]
    public ProceduralLineRenderer predictionProceduralLine;
    public ProceduralLineRenderer originProceduralLine;
    public ProceduralLineRenderer apogeeProceduralLine;
    public ProceduralLineRenderer perigeeProceduralLine;

    [Header("Line Colors")]
    public string predictionLineColor = "#2978FF"; // Blue
    public string originLineColor = "#FFFFFF";     // White
    public string apogeeLineColor = "#FF0000";     // Red
    public string perigeeLineColor = "#00FF00";    // Green
    private float lineDisableDistance = 50f;

    /**
    * Initializes line renderers and sets up materials
    **/
    void Awake()
    {
        mainCamera = Camera.main;
        showPredictionLines = true;
        showOriginLines = true;
        showApogeePerigeeLines = true;

        predictionProceduralLine = CreateProceduralLineRenderer("PredictionLine", predictionLineColor);
        originProceduralLine = CreateProceduralLineRenderer("OriginLine", originLineColor);
        apogeeProceduralLine = CreateProceduralLineRenderer("ApogeeLine", apogeeLineColor);
        perigeeProceduralLine = CreateProceduralLineRenderer("PerigeeLine", perigeeLineColor);

        cameraMovement = CameraMovement.Instance;
        thrustController = ThrustController.Instance;
        uIManager = UIManager.Instance;
    }

    void Update()
    {
        if (thrustController != null)
        {
            isThrusting = thrustController.IsThrusting;
        }
    }

    /** 
    * Stops the prediction coroutine when this object is destroyed 
    **/
    void OnDestroy()
    {
        if (predictionCoroutine != null)
        {
            StopCoroutine(predictionCoroutine);
        }
    }

    private ProceduralLineRenderer CreateProceduralLineRenderer(string name, string hexColor)
    {
        // Create a new GameObject
        GameObject lineObject = new GameObject(name);

        // Add ProceduralLineRenderer component
        ProceduralLineRenderer lineRenderer = lineObject.AddComponent<ProceduralLineRenderer>();

        // Set line color
        lineRenderer.SetLineColor(hexColor);

        // Optionally set other defaults like width
        lineRenderer.SetLineWidth(0.1f);

        return lineRenderer;
    }

    /**
    * Assigns the NBody to be tracked by this TrajectoryRenderer.
    * @param body - Nbody the line renders switch to.
    **/
    public void SetTrackedBody(NBody body)
    {
        trackedBody = body;

        if (trackedBody != null)
        {
            predictionCoroutine = StartCoroutine(RecomputeTrajectory());
        }
    }

    /** 
    * Recomputes the prediction, origin, and apogee/perigee line renders using the GPU
    **/
    public IEnumerator RecomputeTrajectory()
    {
        Vector3 lastPosition = trackedBody.transform.position;
        while (true)
        {
            if (trackedBody == null)
                yield return new WaitForSeconds(0.1f);

            if (cameraMovement == null || cameraMovement.targetBody != trackedBody)
            {
                predictionProceduralLine.Clear();
                originProceduralLine.Clear();
                apogeeProceduralLine.Clear();
                perigeeProceduralLine.Clear();
                yield return new WaitForSeconds(0.1f);
                continue;
            }

            float eccentricity, semiMajorAxis, orbitalPeriod = 0f;

            trackedBody.ComputeOrbitalElements(out semiMajorAxis, out eccentricity, trackedBody.centralBodyMass);
            bool isElliptical = eccentricity < 1f;
            if (showPredictionLines && (update || isThrusting || orbitIsDirty || (isElliptical && (predictionSteps == 5000 || predictionSteps == 3000) && !isThrusting)))
            {
                if (isElliptical)
                {
                    float gravitationalParameter = PhysicsConstants.G * trackedBody.centralBodyMass;
                    orbitalPeriod = 2f * Mathf.PI * Mathf.Sqrt(Mathf.Pow(semiMajorAxis, 3) / gravitationalParameter);

                    // Adjust prediction steps to cover the full orbital loop
                    predictionSteps = Mathf.Clamp(
                        Mathf.CeilToInt(orbitalPeriod / predictionDeltaTime),
                        1,
                        30000
                    );
                }
                else
                {
                    // For hyperbolic orbits, use a fixed number of steps
                    predictionSteps = 5000;
                }

                if (isThrusting)
                {
                    predictionSteps = 3000;
                }

                trackedBody.CalculatePredictedTrajectoryGPU_Async(predictionSteps, predictionDeltaTime, (resultList) =>
                {
                    var fullTrajectory = resultList.ToArray();

                    // Clip the trajectory at the first planet/central body hit
                    var clippedPoints = ClipTrajectory(fullTrajectory);

                    // Finally, update your line
                    predictionProceduralLine.UpdateLine(clippedPoints);
                });

                orbitIsDirty = false;
                if (update) update = false;
            }

            if (showApogeePerigeeLines && Time.time >= apogeePerigeeUpdateTime)
            {
                trackedBody.GetOrbitalApogeePerigee(trackedBody.centralBodyMass, out Vector3 apogeePosition, out Vector3 perigeePosition);

                if (apogeeProceduralLine != null && perigeeProceduralLine != null)
                {
                    apogeeProceduralLine.UpdateLine(new Vector3[] { apogeePosition, Vector3.zero });
                    perigeeProceduralLine.UpdateLine(new Vector3[] { perigeePosition, Vector3.zero });

                    if (apogeeText != null && perigeeText != null)
                    {
                        float apogeeAltitude = (apogeePosition.magnitude - 637.1f) * 10f; // Convert to kilometers
                        float perigeeAltitude = (perigeePosition.magnitude - 637.1f) * 10f; // Convert to kilometers

                        UpdateApogeePerigeeUI(apogeeAltitude, perigeeAltitude);
                    }
                }
                apogeePerigeeUpdateTime = Time.time + updateIntervalApogeePerigee;
            }

            if (showPredictionLines)
            {
                float distanceToCamera = Vector3.Distance(mainCamera.transform.position, trackedBody.transform.position);
                bool show = distanceToCamera > lineDisableDistance;
                if (!show)
                {
                    predictionProceduralLine.SetVisibility(false);
                    originProceduralLine.SetVisibility(false);
                    apogeeProceduralLine.SetVisibility(false);
                    perigeeProceduralLine.SetVisibility(false);
                }
                else
                {
                    predictionProceduralLine.SetVisibility(true);
                    originProceduralLine.SetVisibility(true);
                    apogeeProceduralLine.SetVisibility(true);
                    perigeeProceduralLine.SetVisibility(true);
                }
            }

            if (originProceduralLine != null && showOriginLines)
            {
                originProceduralLine.UpdateLine(new Vector3[] { trackedBody.transform.position, Vector3.zero });
            }

            if (isThrusting)
            {
                if (Time.timeScale >= 50)
                {
                    yield return new WaitForSeconds(3f);
                }
                yield return new WaitForSeconds(1f);
            }
            yield return new WaitForSeconds(.1f);
        }

    }

    private Vector3[] ClipTrajectory(Vector3[] points)
    {
        // Safety checks
        if (points == null || points.Length < 2)
            return points;

        List<Vector3> clippedPoints = new List<Vector3>();
        // Always include the first point
        clippedPoints.Add(points[0]);

        for (int i = 1; i < points.Length; i++)
        {
            Vector3 start = points[i - 1];
            Vector3 end = points[i];
            Vector3 dir = end - start;
            float dist = dir.magnitude;

            // Raycast from 'start' to 'end'
            if (Physics.Raycast(start, dir.normalized, out RaycastHit hit, dist))
            {
                // Check if we hit a planet or central body
                if (hit.collider.CompareTag("CentralBody"))
                {
                    // Add the intersection point and then stop
                    clippedPoints.Add(hit.point);
                    break;
                }
            }

            // If no collision, just add the next point
            clippedPoints.Add(end);
        }

        return clippedPoints.ToArray();
    }

    /**
    * Updates the UI elements for apogee and perigee.
    * @param apogee - Farthest orbit path distance from planet
    * @param timeScale - Closest orbit path distance to planet
    **/
    private void UpdateApogeePerigeeUI(float apogee, float perigee)
    {
        if (apogeeText != null)
        {
            apogeeText.text = $"Apogee: {apogee:F2} km";
        }

        if (perigeeText != null)
        {
            if (perigee < 0)
            {
                perigee = 0;
            }
            perigeeText.text = $"Perigee: {perigee:F2} km";
        }
    }

    /**
    * Adjusts the trajectory prediction settings based on time scale.
    * @param timeScale - The current time slider value for simulation speed.
    **/
    public void AdjustPredictionSettings(float timeScale)
    {
        float distance = transform.position.magnitude;
        float speed = 300f;
        float baseDeltaTime = 0.5f;
        float minDeltaTime = 0.5f;
        float maxDeltaTime = 3f;

        float adjustedDelta = baseDeltaTime * (1 + distance / 1000f) / (1 + speed / 10f);
        adjustedDelta = Mathf.Clamp(adjustedDelta, minDeltaTime, maxDeltaTime);

        predictionDeltaTime = adjustedDelta;
        predictionSteps = 5000;
    }

    /**
    * Sets the enabled state of specific LineRenderers associated with this NBody.
    * @param showPrediction Whether to show/hide the prediction lines (predictionRenderer, activeRenderer, backgroundRenderer).
    * @param showOrigin Whether to show/hide the origin line.
    * @param showApogeePerigee Whether to show/hide the apogee/perigee lines and panel.
    **/
    public void SetLineVisibility(bool showPrediction, bool showOrigin, bool showApogeePerigee)
    {
        showPredictionLines = showPrediction;
        showOriginLines = showOrigin;
        showApogeePerigeeLines = showApogeePerigee;

        if (!showPrediction && predictionProceduralLine != null)
        {
            predictionProceduralLine.Clear();
        }

        if (!showOrigin && originProceduralLine != null)
        {
            originProceduralLine.Clear();
        }

        if (apogeeProceduralLine != null && perigeeProceduralLine != null)
        {
            if (!showApogeePerigee)
            {
                apogeeProceduralLine.Clear();
                perigeeProceduralLine.Clear();
            }

            if (uIManager != null)
            {
                uIManager.ShowApogeePerigeePanel(showApogeePerigeeLines);
            }
        }

        // Re-run RecomputeTrajectory to show lines when reset
        if (showPredictionLines)
        {
            orbitIsDirty = true;
        }
        else
        {
            orbitIsDirty = false;
        }
    }
}
