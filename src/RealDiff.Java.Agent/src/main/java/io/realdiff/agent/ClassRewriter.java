package io.realdiff.agent;

import java.io.PrintWriter;
import java.io.StringWriter;
import java.lang.instrument.IllegalClassFormatException;
import java.nio.file.Path;
import java.util.HashMap;
import java.util.Map;
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
        "enter",
        FRAME_TYPE,
        new Type[] {
            Type.getType(String.class),
            Type.getType(Object[].class),
            Type.getType(String[].class),
            Type.getType(String.class),
            Type.getType(String.class),
            Type.INT_TYPE,
            Type.BOOLEAN_TYPE,
            Type.BOOLEAN_TYPE,
            Type.BOOLEAN_TYPE,
            Type.getType(String.class)
        });
    private static final Method EXIT_METHOD = new Method(
        "exit", Type.VOID_TYPE, new Type[] { FRAME_TYPE, Type.getType(Object.class), THROWABLE_TYPE });
    private static final Method EXIT_FUTURE_METHOD = new Method(
        "exitFuture",
        Type.VOID_TYPE,
        new Type[] { FRAME_TYPE, Type.getType(java.util.concurrent.CompletableFuture.class) });

    byte[] rewrite(String className, byte[] original, ClassLoader loader) throws IllegalClassFormatException {
        String module = className.substring(0, className.indexOf('/') < 0 ? className.length() : className.indexOf('/'));
        return rewrite(className, original, loader, module);
    }

    byte[] rewrite(String className, byte[] original, ClassLoader loader, String module)
        throws IllegalClassFormatException {
        return rewrite(className, original, loader, module, null, null);
    }

    byte[] rewrite(
        String className,
        byte[] original,
        ClassLoader loader,
        String module,
        Path outputLocation,
        JavaSourceResolver sourceResolver) throws IllegalClassFormatException {
        try {
            ClassReader reader = new ClassReader(original);
            SourceMetadata sourceMetadata = SourceMetadata.read(reader);
            String resolvedSourcePath = sourceResolver == null
                ? null
                : sourceResolver.resolve(outputLocation, reader.getClassName(), sourceMetadata.sourceFile);
            ClassWriter writer = new LoaderAwareClassWriter(reader, ClassWriter.COMPUTE_FRAMES | ClassWriter.COMPUTE_MAXS, loader);
            ClassVisitor visitor = new RewritingClassVisitor(
                writer,
                reader.getClassName(),
                sourceMetadata,
                module,
                resolvedSourcePath,
                sourceResolver == null);
            reader.accept(visitor, ClassReader.EXPAND_FRAMES);
            byte[] rewritten = writer.toByteArray();
            verify(className, rewritten, loader);
            return rewritten;
        } catch (IllegalClassFormatException exception) {
            throw exception;
        } catch (RuntimeException exception) {
            IllegalClassFormatException wrapped = new IllegalClassFormatException(
                "RealDiff failed to rewrite class " + className + ": " + exception.getMessage());
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
                "RealDiff verification failed for class " + className + ": " + report);
        }
    }

    private static final class RewritingClassVisitor extends ClassVisitor {
        private final String owner;
        private final SourceMetadata sourceMetadata;
        private final String module;
        private final String resolvedSourcePath;
        private final boolean allowPackageRelativeFallback;

        RewritingClassVisitor(
            ClassVisitor delegate,
            String owner,
            SourceMetadata sourceMetadata,
            String module,
            String resolvedSourcePath,
            boolean allowPackageRelativeFallback) {
            super(Opcodes.ASM9, delegate);
            this.owner = owner;
            this.sourceMetadata = sourceMetadata;
            this.module = module;
            this.resolvedSourcePath = resolvedSourcePath;
            this.allowPackageRelativeFallback = allowPackageRelativeFallback;
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
                || name.equals("<clinit>")
                || name.equals("<init>") && sourceMetadata.isHarness()) {
                return delegate;
            }

            return new ExitOnAllPathsAdapter(
                delegate,
                access,
                name,
                descriptor,
                owner,
                sourceMetadata.location(
                    owner, name, descriptor, resolvedSourcePath, allowPackageRelativeFallback),
                sourceMetadata.isHarness(),
                sourceMetadata.isTestRoot(name, descriptor),
                module,
                sourceMetadata.parameterNames(name, descriptor));
        }
    }

    private static final class ExitOnAllPathsAdapter extends AdviceAdapter {
        private final String methodFullName;
        private final Type returnType;
        private final SourceLocation sourceLocation;
        private final boolean harness;
        private final boolean testRoot;
        private final String module;
        private final String[] parameterNames;
        private final Label bodyStart = new Label();
        private final Label bodyEnd = new Label();
        private final Label exceptionHandler = new Label();
        private int frameLocal;
        private int returnLocal = -1;

        ExitOnAllPathsAdapter(
            MethodVisitor delegate,
            int access,
            String name,
            String descriptor,
            String owner,
            SourceLocation sourceLocation,
            boolean harness,
            boolean testRoot,
            String module,
            String[] parameterNames) {
            super(Opcodes.ASM9, delegate, access, name, descriptor);
            methodFullName = owner.replace('/', '.') + "." + name + descriptor;
            returnType = Type.getReturnType(descriptor);
            this.sourceLocation = sourceLocation;
            this.harness = harness;
            this.testRoot = testRoot;
            this.module = module;
            this.parameterNames = parameterNames;
        }

        @Override
        protected void onMethodEnter() {
            push(methodFullName);
            loadArgArray();
            push(parameterNames.length);
            newArray(Type.getType(String.class));
            for (int index = 0; index < parameterNames.length; index++) {
                dup();
                push(index);
                push(parameterNames[index]);
                arrayStore(Type.getType(String.class));
            }
            if (sourceLocation.filePath == null) {
                visitInsn(ACONST_NULL);
            } else {
                push(sourceLocation.filePath);
            }
            push(sourceLocation.resolution);
            push(sourceLocation.line);
            push(harness);
            push(testRoot);
            push(returnType.getSort() == Type.VOID);
            push(module);
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

            if (returnType.equals(Type.getType(java.util.concurrent.CompletableFuture.class))) {
                loadLocal(frameLocal);
                loadLocal(returnLocal, returnType);
                invokeStatic(HOOKS_TYPE, EXIT_FUTURE_METHOD);
                loadLocal(returnLocal, returnType);
                return;
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

    private static final class SourceMetadata {
        private String sourceFile;
        private boolean hasLineNumbers;
        private final Map<String, Integer> firstLines = new HashMap<>();
        private final Map<String, Boolean> testRoots = new HashMap<>();
        private final Map<String, String[]> parameterNames = new HashMap<>();

        static SourceMetadata read(ClassReader reader) {
            SourceMetadata metadata = new SourceMetadata();
            reader.accept(new ClassVisitor(Opcodes.ASM9) {
                @Override
                public void visitSource(String source, String debug) {
                    metadata.sourceFile = source;
                }

                @Override
                public MethodVisitor visitMethod(
                    int access,
                    String name,
                    String descriptor,
                    String signature,
                    String[] exceptions) {
                    String key = name + descriptor;
                    Type[] argumentTypes = Type.getArgumentTypes(descriptor);
                    String[] names = new String[argumentTypes.length];
                    for (int index = 0; index < names.length; index++) names[index] = "arg" + index;
                    Map<Integer, Integer> slots = new HashMap<>();
                    int slot = (access & Opcodes.ACC_STATIC) == 0 ? 1 : 0;
                    for (int index = 0; index < argumentTypes.length; index++) {
                        slots.put(slot, index);
                        slot += argumentTypes[index].getSize();
                    }
                    metadata.parameterNames.put(key, names);
                    return new MethodVisitor(Opcodes.ASM9) {
                        private int parameterIndex;

                        @Override
                        public void visitParameter(String parameterName, int parameterAccess) {
                            if (parameterName != null && parameterIndex < names.length) names[parameterIndex] = parameterName;
                            parameterIndex++;
                        }

                        @Override
                        public void visitLocalVariable(
                            String variableName,
                            String variableDescriptor,
                            String variableSignature,
                            Label start,
                            Label end,
                            int variableIndex) {
                            Integer argumentIndex = slots.get(variableIndex);
                            if (argumentIndex != null && names[argumentIndex].startsWith("arg")) {
                                names[argumentIndex] = variableName;
                            }
                        }

                        @Override
                        public void visitLineNumber(int line, Label start) {
                            metadata.hasLineNumbers = true;
                            metadata.firstLines.merge(key, line, Math::min);
                        }

                        @Override
                        public org.objectweb.asm.AnnotationVisitor visitAnnotation(String annotation, boolean visible) {
                            if (isTestAnnotation(annotation)) {
                                metadata.testRoots.put(key, true);
                            }
                            return null;
                        }
                    };
                }
            }, ClassReader.SKIP_FRAMES);
            return metadata;
        }

        boolean isHarness() {
            return !testRoots.isEmpty();
        }

        boolean isTestRoot(String name, String descriptor) {
            return testRoots.getOrDefault(name + descriptor, false);
        }

        String[] parameterNames(String name, String descriptor) {
            return parameterNames.getOrDefault(name + descriptor, new String[0]);
        }

        private static boolean isTestAnnotation(String descriptor) {
            return descriptor.equals("Lorg/junit/Test;")
                || descriptor.equals("Lorg/junit/jupiter/api/Test;")
                || descriptor.equals("Lorg/junit/jupiter/params/ParameterizedTest;")
                || descriptor.equals("Lorg/testng/annotations/Test;");
        }

        SourceLocation location(
            String owner,
            String name,
            String descriptor,
            String resolvedSourcePath,
            boolean allowPackageRelativeFallback) {
            Integer firstLine = firstLines.get(name + descriptor);
            String path = resolvedSourcePath;
            if (path == null && allowPackageRelativeFallback && sourceFile != null) {
                path = sourcePath(owner, sourceFile);
            }
            if (path != null && firstLine != null) {
                return new SourceLocation(path, "debugInfo", firstLine);
            }
            if (path != null && hasLineNumbers) {
                return new SourceLocation(path, "declaringType", 0);
            }
            if (sourceFile == null || !hasLineNumbers) {
                return new SourceLocation(null, "debugInfoMissing", 0);
            }
            return new SourceLocation(null, "unresolved", 0);
        }

        private static String sourcePath(String owner, String sourceFile) {
            int separator = owner.lastIndexOf('/');
            return separator < 0 ? sourceFile : owner.substring(0, separator + 1) + sourceFile;
        }
    }

    private static final class SourceLocation {
        private final String filePath;
        private final String resolution;
        private final int line;

        SourceLocation(String filePath, String resolution, int line) {
            this.filePath = filePath;
            this.resolution = resolution;
            this.line = line;
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