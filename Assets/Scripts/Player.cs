using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Player : MonoBehaviour
{
    private enum AnimState
    {
        Stand,
        Run,
        Jump,
        Fall,
    }

    private const float runAcceleration = 15;
    private const float maxRunSpeed = 7;
    private const float jumpForce = 8;
    private const float walljumpUpForce = 8 / 1.414f;
    private const float walljumpSideForce = 8 / 1.414f;
    private const float gravityForce = 40;
    private const float maxFallSpeed = 50;
    private const float maxJumpTime = 0.3f;
    private const float maxWalljumpTime = 0.3f;
    private const float groundForceFriction = 0.8f;
    private const float pitchVariation = 0.15f;

    private Rigidbody2D rb;
    private EdgeCollider2D ec;

    private bool triggerWasHeld = false;
    private bool jumpQueued = false;
    private bool jumpReleaseQueued = false;
    private bool grabQueued = false;
    private float xForce = 0;

    private bool canJump = false;
    private bool wasOnGround = false;
    private bool jumpFloating = false;
    private int walljumpDir = 0;
    private float jumpTimer = 0;
    private Coroutine crtCancelQueuedJump;
    private const float jumpBufferTime = 0.1f; //time before hitting ground a jump will still be queued
    private const float jumpGraceTime = 0.1f; //time after leaving ground player can still jump (coyote time)

    private const float runFrameTime = 0.1f;
    private SpriteRenderer sr;
    private AnimState animState = AnimState.Stand;
    private int animFrame = 0;
    private float frameTime; //max time of frame
    private float frameTimer; //goes from frameTime down to 0
    public bool facingLeft = false; //for animation (images face right)
    public Sprite standSprite;
    public Sprite jumpSprite;
    public Sprite fallSprite;
    public Sprite[] runSprites;
    public Sprite standHoldSprite;
    public Sprite jumpHoldSprite;
    public Sprite fallHoldSprite;
    public Sprite[] runHoldSprites;

    private AudioSource audioSource;
    public AudioClip jumpSound;
    public AudioClip landSound;

    private Persistent persistent;

    private Holdable heldItem = null;

    private void Start()
    {
        rb = gameObject.GetComponent<Rigidbody2D>();
        ec = gameObject.GetComponent<EdgeCollider2D>();
        sr = gameObject.GetComponent<SpriteRenderer>();
        audioSource = gameObject.GetComponent<AudioSource>();

        Persistent[] persistents = FindObjectsOfType<Persistent>();
        foreach (Persistent p in persistents)
        {
            if (!p.destroying)
            {
                persistent = p;
                break;
            }
        }
    }

    private void Update()
    {
        if (Input.GetButtonDown("Jump"))
        {
            TryStopCoroutine(crtCancelQueuedJump);
            jumpQueued = true;
            crtCancelQueuedJump = StartCoroutine(CancelQueuedJump());
        }

        if (Input.GetButtonUp("Jump"))
        {
            jumpReleaseQueued = true;
        }

        if (Input.GetButtonDown("Grab"))
        {
            grabQueued = true;
        }

        bool triggerHeld = Input.GetAxis("LTrigger") > 0 || Input.GetAxis("RTrigger") > 0;
        bool triggerPressed = !triggerWasHeld && triggerHeld;
        if (triggerPressed)
        {
            grabQueued = true;
        }
        triggerWasHeld = triggerHeld;

        sr.flipX = facingLeft;
        AdvanceAnim();
        sr.sprite = GetAnimSprite();
    }

    private Collider2D RaycastTiles(Vector2 startPoint, Vector2 endPoint)
    {
        RaycastHit2D hit = Physics2D.Raycast(startPoint, endPoint - startPoint, Vector2.Distance(startPoint, endPoint), LayerMask.GetMask("Tiles"));
        return hit.collider;
    }

    private bool CheckSide(int point0, int point1, Vector2 direction)
    {
        Vector2 startPoint = rb.position + ec.points[point0] + direction * 0.02f;
        Vector2 endPoint = rb.position + ec.points[point1] + direction * 0.02f;
        Collider2D collider = RaycastTiles(startPoint, endPoint);
        return collider != null;
    }

    private void FixedUpdate()
    {
        float xInput = Input.GetAxis("Horizontal");
        float prevXVel = rb.velocity.x;
        float xVel;
        float dx = runAcceleration * Time.fixedDeltaTime * xInput;
        if (prevXVel != 0 && Mathf.Sign(xInput) != Mathf.Sign(prevXVel))
        {
            xVel = 0;
        }
        else
        {
            xVel = prevXVel + dx;
            float speedCap = Mathf.Abs(xInput * maxRunSpeed);
            xVel = Mathf.Clamp(xVel, -speedCap, speedCap);
        }

        if (xForce != 0)
        {
            //if not moving: keep xForce
            if (xInput == 0)
            {
                xVel = xForce;
            }
            else
            {
                if (Mathf.Sign(xInput) == Mathf.Sign(xForce))
                {
                    //moving in same direction
                    if (Mathf.Abs(xVel) >= Mathf.Abs(xForce))
                    {
                        //xVel has higher magnitude: set xForce to 0 (replace little momentum push)
                        xForce = 0;
                    }
                    else
                    {
                        //xForce has higher magnitude: set xVel to xForce (pushed by higher momentum)
                        xVel = xForce;
                    }
                }
                else
                {
                    //moving in other direction
                    //decrease xForce by dx (stopping at 0)
                    float prevSign = Mathf.Sign(xForce);
                    xForce += dx;
                    if (Mathf.Sign(xForce) != prevSign)
                    {
                        xForce = 0;
                    }
                    xVel = xForce;
                }
            }
        }

        if (xInput != 0)
        {
            facingLeft = xInput < 0;
        }
        else if (xVel != 0)
        {
            //facingLeft = xVel < 0;
        }

        float yVel;

        bool onGround = CheckSide(0, 1, Vector2.down); //BoxcastTiles(Vector2.down, 0.15f) != null;
        bool onCeiling = CheckSide(2, 3, Vector2.up); //BoxcastTiles(Vector2.up, 0.15f) != null;

        if (onGround)
        {
            canJump = true;

            if (xForce != 0)
            {
                xForce *= groundForceFriction;
                if (Mathf.Abs(xForce) < 0.05f)
                {
                    xForce = 0;
                }
            }

            if (rb.velocity.y < 0)
            {
                PlaySound(landSound);
            }
            yVel = 0;

            SetAnimState(xVel == 0 ? AnimState.Stand : AnimState.Run);
        }
        else
        {
            yVel = Mathf.Max(rb.velocity.y - gravityForce * Time.fixedDeltaTime, -maxFallSpeed);

            if (wasOnGround)
            {
                StartCoroutine(LeaveGround());
            }

            if (yVel < 0)
            {
                SetAnimState(AnimState.Fall);
            }
        }
        wasOnGround = onGround;

        if (onCeiling && yVel > 0)
        {
            yVel = 0;
            PlaySound(landSound);
        }

        if (jumpQueued)
        {
            bool onRight = CheckSide(1, 2, Vector2.right);
            bool onLeft = CheckSide(3, 4, Vector2.left);
            if (!onGround)
            {
                if (onRight)
                {
                    jumpFloating = false;
                    StopCancelQueuedJump();
                    jumpQueued = false;
                    canJump = false;
                    xForce = 0;
                    walljumpDir = -1;
                    jumpTimer = 0;
                    PlaySound(jumpSound);
                    SetAnimState(AnimState.Jump);
                }
                else if (onLeft)
                {
                    jumpFloating = false;
                    StopCancelQueuedJump();
                    jumpQueued = false;
                    canJump = false;
                    xForce = 0;
                    walljumpDir = 1;
                    jumpTimer = 0;
                    PlaySound(jumpSound);
                    SetAnimState(AnimState.Jump);
                }
            }

            if (canJump)
            {
                StopCancelQueuedJump();
                jumpQueued = false;
                canJump = false;
                xForce = 0;
                jumpFloating = true;
                jumpTimer = 0;
                PlaySound(jumpSound);
                SetAnimState(AnimState.Jump);
            }
        }

        if (jumpFloating)
        {
            yVel = jumpForce; //Mathf.Max(jumpForce, yVel + jumpForce);
            jumpTimer += Time.fixedDeltaTime;
            if (jumpTimer >= maxJumpTime)
            {
                jumpFloating = false;
            }
        }

        if (walljumpDir != 0)
        {
            yVel = walljumpUpForce;
            xVel = walljumpSideForce * walljumpDir;
            jumpTimer += Time.fixedDeltaTime;
            if (jumpTimer >= maxWalljumpTime)
            {
                walljumpDir = 0;
            }
        }

        if (jumpReleaseQueued)
        {
            if (jumpFloating)
            {
                jumpFloating = false;
                jumpReleaseQueued = false;
            }

            if (walljumpDir != 0)
            {
                walljumpDir = 0;
            }

            if (!jumpQueued)
            {
                jumpReleaseQueued = false;
            }
        }

        Vector2 vel = new Vector2(xVel, yVel);
        rb.velocity = vel;
        rb.MovePosition(rb.position + vel * Time.fixedDeltaTime);
    }

    private void OnCollisionEnter2D(Collision2D collision)
    {
        if (!gameObject.activeSelf) return;

        GameObject collider = collision.collider.gameObject;

        if (collider.layer == LayerMask.NameToLayer("Tiles"))
        {
            if (collision.GetContact(0).normal.x != 0)
            {
                //against wall, not ceiling
                //PlaySound(bonkSound);
                if (xForce != 0)
                {
                    PlaySound(landSound);
                }
                xForce = 0;
            }
        }
    }

    private void OnTriggerEnter2D(Collider2D collision)
    {
        if (!gameObject.activeSelf) return;

        GameObject collider = collision.gameObject;

        /*Gem gem = collider.GetComponent<Gem>();
        if (gem != null)
        {
            Destroy(collider);
            PlaySound(collectGemSound);
            Instantiate(collectParticlePrefab, collider.transform.position, Quaternion.identity);
            levelGems++;
        }*/
    }

    private Sprite GetAnimSprite()
    {
        bool holding = heldItem != null;
        switch (animState)
        {
            case AnimState.Stand:
                return holding ? standHoldSprite : standSprite;
            case AnimState.Run:
                return holding ? runHoldSprites[animFrame] : runSprites[animFrame];
            case AnimState.Jump:
                return holding ? jumpHoldSprite : jumpSprite;
            case AnimState.Fall:
                return holding ? fallHoldSprite : fallSprite;
        }
        return standSprite;
    }

    private void TryStopCoroutine(Coroutine crt)
    {
        if (crt != null)
        {
            StopCoroutine(crt);
        }
    }

    private void StopCancelQueuedJump()
    {
        TryStopCoroutine(crtCancelQueuedJump);
    }

    private IEnumerator CancelQueuedJump()
    {
        yield return new WaitForSeconds(jumpBufferTime);
        jumpQueued = false;
    }

    private IEnumerator LeaveGround()
    {
        yield return new WaitForSeconds(jumpGraceTime);
        canJump = false;
    }

    private void SetAnimState(AnimState state)
    {
        animState = state;
    }

    private void AdvanceAnim()
    {
        if (animState == AnimState.Run)
        {
            frameTime = runFrameTime;
            AdvanceFrame(runSprites.Length);
        }
        else
        {
            animFrame = 0;
            frameTimer = frameTime;
        }
    }

    private void AdvanceFrame(int numFrames)
    {
        if (animFrame >= numFrames)
        {
            animFrame = 0;
        }

        frameTimer -= Time.deltaTime;
        if (frameTimer <= 0)
        {
            frameTimer = frameTime;
            animFrame = (animFrame + 1) % numFrames;
        }
    }

    public void PlaySound(AudioClip sound, bool randomizePitch = false)
    {
        if (sound == null)
        {
            print("missing sound!");
            return;
        }
        if (randomizePitch)
        {
            audioSource.pitch = Random.Range(1 - pitchVariation, 1 + pitchVariation);
        }
        else
        {
            audioSource.pitch = 1;
        }
        audioSource.PlayOneShot(sound);
    }
}
