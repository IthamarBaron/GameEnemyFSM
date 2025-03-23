using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;


[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(Animator))]

public class EnemyAI : MonoBehaviour
{
    public enum EnemyState { Roam, Attack, Search, Chase }

    [Header("References")]
    public NavMeshAgent agent;
    public Transform player;
    public Transform nodesParent;

    [Header("Vision Settings")]
    public float viewRadius = 20f;
    [Range(0, 360)] public float viewAngle = 120f;
    public LayerMask playerMask;
    public LayerMask obstacleMask;

    [Header("Node Settings")]
    public float searchPrecision = 5f;
    private List<Transform> nodes = new List<Transform>();
    private Queue<Transform> recentNodes = new Queue<Transform>();

    public float mapBorder = 70f; // Distance from 0,0 before triggering CHASE


    // Timers for state transitions
    private float fovTimer = 0f;
    private float lostPlayerTimer = 0f;
    private float searchTimer = 0f;
    private Vector3 lastKnownPlayerPosition;
    private Animator animator;
    private EnemyState currentState;

    void Start()
    {
        agent = GetComponent<NavMeshAgent>();
        animator = GetComponent<Animator>();

        foreach (Transform node in nodesParent)
            nodes.Add(node);

        currentState = EnemyState.Roam;
        SetNextRoamDestination();
    }

    void Update()
    {

        if (!IsPlayerWithinMapBorder())
        {
            if (currentState != EnemyState.Chase)
            {
                currentState = EnemyState.Chase;
                Debug.Log("Player exited map border: transitioning to -> CHASE");
            }
        }

        switch (currentState)
        {
            case EnemyState.Roam:
                agent.speed = 3.6f;
                HandleRoamState();
                break;
            case EnemyState.Attack:
                agent.speed = 4.8f;
                HandleAttackState();
                break;
            case EnemyState.Search:
                agent.speed = 3f;
                HandleSearchState();
                break;
            case EnemyState.Chase:
                agent.speed = 20f;
                agent.acceleration = 10f;    
                agent.angularSpeed = 120f;  
                agent.SetDestination(player.position);

                break;
        }

        HandleStateTransitions();
        animator.SetFloat("speed", agent.velocity.magnitude);

    }
    bool IsPlayerWithinMapBorder()
    {
        Vector3 playerPos = new Vector3(player.position.x, 0, player.position.z);
        return playerPos.magnitude <= mapBorder;
    }

    #region FSM State Handling

    void HandleRoamState()
    {
        if (!agent.pathPending && agent.remainingDistance <= searchPrecision)
        {
            SetNextRoamDestination();
        }
    }

    void HandleAttackState()
    {
        agent.SetDestination(player.position);
        lastKnownPlayerPosition = player.position;
    }

    void HandleSearchState()
    {
        if (!agent.pathPending && agent.remainingDistance <= 0.5f && !isSearching)
        {
            StartCoroutine(SearchLookRoutine());
        }
    }
    private bool isSearching = false;

    IEnumerator<WaitForSeconds> SearchLookRoutine()
    {
        isSearching = true;

        float[] anglesToLook = { -60f, 0f, 60f }; // look left, forward, then right
        Quaternion originalRotation = transform.rotation;

        foreach (float angle in anglesToLook)
        {
            Quaternion lookRotation = Quaternion.Euler(0, transform.eulerAngles.y + angle, 0);
            float elapsed = 0f;
            while (elapsed < 0.4f)
            {
                transform.rotation = Quaternion.Slerp(transform.rotation, lookRotation, Time.deltaTime * 5f);
                elapsed += Time.deltaTime;
                yield return null;
            }
            yield return new WaitForSeconds(0.4f);
        }

        Debug.Log("Search routine completed, transitioning to -> Roam");
        currentState = EnemyState.Roam;
        SetNextRoamDestination();
        isSearching = false;
    }

    #endregion



    void HandleStateTransitions()
    {
        bool seesPlayer = PlayerInSight();

        switch (currentState)
        {
            case EnemyState.Roam:
                if (seesPlayerContinuously(0.2f))
                {
                    currentState = EnemyState.Attack;
                    Debug.Log("Detected player: transitioning to -> ATTACK");
                }
                break;

            case EnemyState.Attack:
                if (!seesPlayerContinuously(2f))
                {
                    currentState = EnemyState.Search;
                    agent.SetDestination(lastKnownPlayerPosition);
                    searchTimer = 0f;
                    Debug.Log("Lost player: transitioning to -> Search");
                }
                break;

            case EnemyState.Search:
                if (PlayerInSight())
                {
                    currentState = EnemyState.Attack;
                    Debug.Log("Player found during search: transitioning to -> ATTACK");
                }
                else if (!agent.pathPending && agent.remainingDistance <= 0.5f)
                {
                    searchTimer += Time.deltaTime;
                    if (searchTimer >= 3f)
                    {
                        currentState = EnemyState.Roam;
                        SetNextRoamDestination();
                        Debug.Log("Search timeout: transitioning to -> Roam");
                    }
                }
                break;
        }
    }



    bool seesPlayerContinuously(float threshold)
    {
        if (PlayerInSight())
        {
            fovTimer += Time.deltaTime;
            lostPlayerTimer = 0f;
        }
        else
        {
            lostPlayerTimer += Time.deltaTime;
            fovTimer = 0f;
        }

        if (currentState == EnemyState.Roam)
            return fovTimer >= threshold;

        if (currentState == EnemyState.Attack)
            return lostPlayerTimer < threshold;

        return false;
    }

    bool PlayerInSight()
    {
        Collider[] targetsInViewRadius = Physics.OverlapSphere(transform.position, viewRadius, playerMask);
        foreach (Collider target in targetsInViewRadius)
        {
            Vector3 dirToTarget = (target.transform.position - transform.position).normalized;
            if (Vector3.Angle(transform.forward, dirToTarget) < viewAngle / 2)
            {
                float distToTarget = Vector3.Distance(transform.position, target.transform.position);
                if (!Physics.Raycast(transform.position, dirToTarget, distToTarget, obstacleMask))
                    return true;
            }
        }
        return false;
    }

    void SetNextRoamDestination()
    {
        Transform nextNode = ChooseNextNode();
        if (nextNode != null)
        {
            agent.SetDestination(nextNode.position);
            QueueNode(nextNode);
        }
    }

    Transform ChooseNextNode()
    {
        Transform chosenNode = null;

        if (recentNodes.Count % 2 == 0) // Closest node
            chosenNode = GetClosestNode(transform.position);
        else
            chosenNode = GetFarNode(transform.position, 30f);

        return chosenNode;
    }

    Transform GetClosestNode(Vector3 position)
    {
        Transform closest = null;
        float minDist = Mathf.Infinity;
        foreach (Transform node in nodes)
        {
            if (recentNodes.Contains(node)) continue;

            float dist = Vector3.Distance(node.position, position);
            if (dist < minDist)
            {
                closest = node;
                minDist = dist;
            }
        }
        return closest;
    }

    Transform GetFarNode(Vector3 position, float minDistance)
    {
        List<Transform> validNodes = new List<Transform>();
        foreach (Transform node in nodes)
        {
            float dist = Vector3.Distance(node.position, position);
            if (dist >= minDistance && !recentNodes.Contains(node))
                validNodes.Add(node);
        }
        if (validNodes.Count > 0)
            return validNodes[Random.Range(0, validNodes.Count)];
        else
            return GetClosestNode(position);
    }





    #region Gizmos Visualization

    void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, viewRadius);

        Vector3 viewAngleA = DirFromAngle(-viewAngle / 2);
        Vector3 viewAngleB = DirFromAngle(viewAngle / 2);

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, transform.position + viewAngleA * viewRadius);
        Gizmos.DrawLine(transform.position, transform.position + viewAngleB * viewRadius);
    }

    Vector3 DirFromAngle(float angleInDegrees)
    {
        angleInDegrees += transform.eulerAngles.y;
        return new Vector3(Mathf.Sin(angleInDegrees * Mathf.Deg2Rad), 0, Mathf.Cos(angleInDegrees * Mathf.Deg2Rad));
    }

    #endregion

    #region Node Queue Management

    void QueueNode(Transform node)
    {
        recentNodes.Enqueue(node);
        if (recentNodes.Count > 5)
            recentNodes.Dequeue();
    }

    #endregion
}
