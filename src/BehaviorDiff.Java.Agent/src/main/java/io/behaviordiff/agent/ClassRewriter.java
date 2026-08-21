package io.behaviordiff.agent;

import java.io.PrintWriter;
import java.io.StringWriter;
import java.lang.instrument.IllegalClassFormatException;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Label;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;
import org.objectweb.asm.commons.AdviceAdapter;
import org.objectweb.asm.commons.Method;
import org.objectweb.asm.util.CheckClassAdapter;

final class ClassRewriter {
    private static final Type HOOKS_TYPE = Type.getType(RuntimeHooks.class);
    private static final Type FRAME_TYPE = Type.getType(CallFrame.class);
    private static final Type THROWABLE_TYPE = Type.getType(Throwable.class);
    private static final Method ENTER_METHOD = new Method(
        "enter", FRAME_TYPE, new Type[] { Type.getType(String.class), Type.getType(Object[].class) });
    private static final Method EXIT_METHOD = new Method(
        "exit", Type.VOID_TYPE, new Type[] { FRAME_TYPE, Type.getType(Object.class), THROWABLE_TYPE });

    byte[] rewrite(String className, byte[] original, ClassLoader loader) throws IllegalClassFormatException {
        try {
            ClassReader reader = new ClassReader(original);
            ClassWriter writer = new LoaderAwareClassWriter(reader, ClassWriter.COMPUTE_FRAMES | ClassWriter.COMPUTE_MAXS, loader);
            ClassVisitor visitor = new RewritingClassVisitor(writer, reader.getClassName());
            reader.accept(visitor, ClassReader.EXPAND_FRAMES);
            byte[] rewritten = writer.toByteArray();
            verify(className, rewritten, loader);
            return rewritten;
        } catch (IllegalClassFormatException exception) {
            throw exception;
        } catch (RuntimeException exception) {
            IllegalClassFormatException wrapped = new IllegalClassFormatException(
                "BehaviorDiff failed to rewrite class " + className + ": " + exception.getMessage());
            wrapped.initCause(exception);
            throw wrapped;
        }
    }

    private static void verify(String className, byte[] rewritten, ClassLoader loader)
        throws IllegalClassFormatException {
        StringWriter diagnostics = new StringWriter();
        CheckClassAdapter.verify(
            new ClassReader(rewritten),
            loader,
            false,
            new PrintWriter(diagnostics));
        String report = diagnostics.toString().trim();
        if (!report.isEmpty()) {
            throw new IllegalClassFormatException(
                "BehaviorDiff verification failed for class " + className + ": " + report);
        }
    }

    private static final class RewritingClassVisitor extends ClassVisitor {
        private final String owner;

        RewritingClassVisitor(ClassVisitor delegate, String owner) {
            super(Opcodes.ASM9, delegate);
            this.owner = owner;
        }

        @Override
        public MethodVisitor visitMethod(
            int access,
            String name,
            String descriptor,
            String signature,
            String[] exceptions) {
            MethodVisitor delegate = super.visitMethod(access, name, descriptor, signature, exceptions);
            if (delegate == null
                || (access & (Opcodes.ACC_ABSTRACT | Opcodes.ACC_NATIVE)) != 0
                || name.equals("<clinit>")) {
                return delegate;
            }

            return new ExitOnAllPathsAdapter(delegate, access, name, descriptor, owner);
        }
    }

    private static final class ExitOnAllPathsAdapter extends AdviceAdapter {
        private final String methodFullName;
        private final Type returnType;
        private final Label bodyStart = new Label();
        private final Label bodyEnd = new Label();
        private final Label exceptionHandler = new Label();
        private int frameLocal;
        private int returnLocal = -1;

        ExitOnAllPathsAdapter(MethodVisitor delegate, int access, String name, String descriptor, String owner) {
            super(Opcodes.ASM9, delegate, access, name, descriptor);
            methodFullName = owner.replace('/', '.') + "." + name + descriptor;
            returnType = Type.getReturnType(descriptor);
        }

        @Override
        protected void onMethodEnter() {
            push(methodFullName);
            loadArgArray();
            invokeStatic(HOOKS_TYPE, ENTER_METHOD);
            frameLocal = newLocal(FRAME_TYPE);
            storeLocal(frameLocal);
            mark(bodyStart);
        }

        @Override
        protected void onMethodExit(int opcode) {
            if (opcode == ATHROW) {
                return;
            }

            if (returnType.getSort() != Type.VOID) {
                if (returnLocal < 0) {
                    returnLocal = newLocal(returnType);
                }
                storeLocal(returnLocal, returnType);
            }

            loadLocal(frameLocal);
            if (returnType.getSort() == Type.VOID) {
                visitInsn(ACONST_NULL);
            } else {
                loadLocal(returnLocal, returnType);
                box(returnType);
            }
            visitInsn(ACONST_NULL);
            invokeStatic(HOOKS_TYPE, EXIT_METHOD);

            if (returnType.getSort() != Type.VOID) {
                loadLocal(returnLocal, returnType);
            }
        }

        @Override
        public void visitMaxs(int maxStack, int maxLocals) {
            mark(bodyEnd);
            visitTryCatchBlock(bodyStart, bodyEnd, exceptionHandler, null);
            mark(exceptionHandler);
            int throwableLocal = newLocal(THROWABLE_TYPE);
            storeLocal(throwableLocal);
            loadLocal(frameLocal);
            visitInsn(ACONST_NULL);
            loadLocal(throwableLocal);
            invokeStatic(HOOKS_TYPE, EXIT_METHOD);
            loadLocal(throwableLocal);
            mv.visitInsn(ATHROW);
            super.visitMaxs(maxStack, maxLocals);
        }
    }

    private static final class LoaderAwareClassWriter extends ClassWriter {
        private final ClassLoader loader;

        LoaderAwareClassWriter(ClassReader reader, int flags, ClassLoader loader) {
            super(reader, flags);
            this.loader = loader;
        }

        @Override
        protected String getCommonSuperClass(String left, String right) {
            try {
                Class<?> leftClass = Class.forName(left.replace('/', '.'), false, loader);
                Class<?> rightClass = Class.forName(right.replace('/', '.'), false, loader);
                if (leftClass.isAssignableFrom(rightClass)) {
                    return left;
                }
                if (rightClass.isAssignableFrom(leftClass)) {
                    return right;
                }
                if (leftClass.isInterface() || rightClass.isInterface()) {
                    return "java/lang/Object";
                }
                do {
                    leftClass = leftClass.getSuperclass();
                } while (!leftClass.isAssignableFrom(rightClass));
                return leftClass.getName().replace('.', '/');
            } catch (ClassNotFoundException exception) {
                throw new IllegalStateException(
                    "cannot resolve frame types " + left + " and " + right,
                    exception);
            }
        }
    }
}