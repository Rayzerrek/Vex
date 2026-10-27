import { Home } from "./home";

export function IntroDemo() {
  return (
    <>
      <div className="intro-screen" aria-hidden="true">
        <div className="intro-glow" />
        <div className="intro-mark">
          <img src="/intro-fox.webp" alt="" />
        </div>
      </div>
      <Home />
      <a className="intro-replay" href="/vex/intro-demo">
        Replay intro
      </a>
    </>
  );
}
